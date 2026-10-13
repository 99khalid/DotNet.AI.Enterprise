using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Application.Leave;

namespace DotNet.AI.Enterprise.WebApi.Endpoints;

/// <summary>
/// The leave-request workflow over HTTP. Server-Sent Events:
/// "step" (one per finished step) → "proposal" (paused, waiting for the employee) or "result", or "error".
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

    private static IAsyncEnumerable<SseItem<object>> Stream(
        IAsyncEnumerable<LeaveWorkflowUpdate> source, ILogger logger, CancellationToken cancellationToken) =>
        SseStreaming.MapAsync(source, ToEvents, logger, new StrongBox<bool>(), cancellationToken);

    /// <summary>Workflow update → SSE event. Shared with the chat endpoint (leave requests routed from the chat).</summary>
    internal static IEnumerable<SseItem<object>> ToEvents(LeaveWorkflowUpdate update)
    {
        yield return update switch
        {
            LeaveStepCompleted step => new SseItem<object>(step, "step"),
            LeaveApprovalNeeded approval => new SseItem<object>(approval, "proposal"),
            LeaveWorkflowFinished finished => new SseItem<object>(finished.Result, "result"),
            _ => new SseItem<object>(update, "unknown")
        };
    }
}
