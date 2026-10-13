using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Application.Leave;

namespace DotNet.AI.Enterprise.WebApi.Endpoints;

/// <summary>
/// The leave-request workflow over HTTP. Server-Sent Events:
/// "step" (one per finished step) → "approval" (paused, waiting for the employee) or "result", or "error".
/// </summary>
public static class LeaveWorkflowEndpoints
{
    private const int MaxInputLength = 4000;

    public static IEndpointRouteBuilder MapLeaveWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/leave-requests/workflow");

        api.MapPost("/", Start);
        api.MapPost("/{runId}/decision", Decide);

        return app;
    }

    private static IResult Start(
        StartLeaveWorkflowRequest request, LeaveWorkflowService workflows, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxInputLength)
            return TypedResults.BadRequest(new ErrorEvent($"Text must be 1–{MaxInputLength} characters."));

        var logger = loggerFactory.CreateLogger(nameof(LeaveWorkflowEndpoints));
        return TypedResults.ServerSentEvents(Stream(workflows.StartAsync(request.Text, cancellationToken), logger, cancellationToken));
    }

    /// <summary>The employee clicked Approve or Reject on the proposal card.</summary>
    private static IResult Decide(
        string runId, ApprovalDecisionRequest decision, LeaveWorkflowService workflows, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!workflows.IsPaused(runId))
            return TypedResults.Conflict(new ErrorEvent("This leave request doesn't exist or was already decided."));

        var logger = loggerFactory.CreateLogger(nameof(LeaveWorkflowEndpoints));
        return TypedResults.ServerSentEvents(Stream(workflows.ResumeAsync(runId, decision.Approved, cancellationToken), logger, cancellationToken));
    }

    private static async IAsyncEnumerable<SseItem<object>> Stream(
        IAsyncEnumerable<LeaveWorkflowUpdate> source, ILogger logger, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Same pattern as the chat stream: errors after the 200 is sent become an "error" event.
        await using var updates = source.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            LeaveWorkflowUpdate update;
            string? error = null;

            try
            {
                if (!await updates.MoveNextAsync()) break;
                update = updates.Current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Leave workflow failed");
                error = ex is OperationCanceledException ? "The AI provider timed out. Try again." : ex.Message;
                update = null!;
            }

            if (error is not null)
            {
                yield return new SseItem<object>(new ErrorEvent(error), "error");
                yield break;
            }

            yield return update switch
            {
                LeaveStepCompleted step => new SseItem<object>(step, "step"),
                LeaveApprovalNeeded approval => new SseItem<object>(approval, "approval"),
                LeaveWorkflowFinished finished => new SseItem<object>(finished.Result, "result"),
                _ => new SseItem<object>(update, "unknown")
            };
        }
    }
}
