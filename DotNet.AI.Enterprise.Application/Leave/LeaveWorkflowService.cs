using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Application.Rag;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace DotNet.AI.Enterprise.Application.Leave;

public abstract record LeaveWorkflowUpdate;

public sealed record LeaveStepCompleted(string Step) : LeaveWorkflowUpdate;

public sealed record LeaveApprovalNeeded(string RunId, LeaveProposal Proposal) : LeaveWorkflowUpdate;

public sealed record LeaveWorkflowFinished(LeaveWorkflowResult Result) : LeaveWorkflowUpdate;

/// <summary>
/// Runs the leave workflow across HTTP requests: the first request runs until the Approval pause,
/// the employee's decision (a second request) resumes the same run.
/// Paused runs live in memory — lost on restart. Durable checkpoints are the Production stage.
/// </summary>
public sealed class LeaveWorkflowService(
    LeaveRequestExtractor extractor,
    LeaveRequestService leave,
    PolicyRetriever retriever,
    ILogger<LeaveWorkflowService> logger)
{
    private static readonly HashSet<string> Steps = ["Extract", "Validate", "NeedsInfo", "Enrich", "Submit"];

    private readonly ConcurrentDictionary<string, (StreamingRun Run, ExternalRequest Request)> _paused = new();

    public bool IsPaused(string runId) => _paused.ContainsKey(runId);

    public async IAsyncEnumerable<LeaveWorkflowUpdate> StartAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var workflow = LeaveWorkflow.Build(extractor, leave, retriever);
        var run = await InProcessExecution.RunStreamingAsync(workflow, text, cancellationToken: cancellationToken);

        await foreach (var update in DrainAsync(run, cancellationToken))
            yield return update;
    }

    /// <summary>The employee's decision from the approval card — a button, never chat text.</summary>
    public async IAsyncEnumerable<LeaveWorkflowUpdate> ResumeAsync(
        string runId, bool approved, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A decision is bound to one paused run and can be used once.
        if (!_paused.TryRemove(runId, out var paused) || !paused.Request.TryGetDataAs<LeaveProposal>(out var proposal))
            throw new InvalidOperationException("This leave request doesn't exist or was already decided.");

        logger.LogInformation("Leave workflow {RunId}: {Decision} {Type} {Start}..{End} ({Days} days)",
            runId, approved ? "APPROVED" : "REJECTED", proposal.Type, proposal.StartDate, proposal.EndDate, proposal.Days);

        await paused.Run.SendResponseAsync(paused.Request.CreateResponse(new LeaveDecision(approved, proposal)));

        await foreach (var update in DrainAsync(paused.Run, cancellationToken))
            yield return update;
    }

    /// <summary>Streams events until the run finishes or pauses for approval.</summary>
    private async IAsyncEnumerable<LeaveWorkflowUpdate> DrainAsync(
        StreamingRun run, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ExternalRequest? approvalRequest = null;

        await foreach (var evt in run.WatchStreamAsync(blockOnPendingRequest: false, cancellationToken))
        {
            switch (evt)
            {
                case ExecutorCompletedEvent step when Steps.Contains(step.ExecutorId):
                    yield return new LeaveStepCompleted(step.ExecutorId);
                    break;
                case RequestInfoEvent request:
                    approvalRequest = request.Request;
                    break;
                case WorkflowOutputEvent { Data: LeaveWorkflowResult result }:
                    yield return new LeaveWorkflowFinished(result);
                    break;
                case ExecutorFailedEvent failed:
                    await run.DisposeAsync();
                    throw new InvalidOperationException($"Step '{failed.ExecutorId}' failed: {failed.Data?.Message}");
            }
        }

        if (approvalRequest is not null && approvalRequest.TryGetDataAs<LeaveProposal>(out var proposal))
        {
            var runId = Guid.NewGuid().ToString("N");
            _paused[runId] = (run, approvalRequest);
            logger.LogInformation("Leave workflow {RunId}: waiting for approval of {Type} {Start}..{End}",
                runId, proposal.Type, proposal.StartDate, proposal.EndDate);
            yield return new LeaveApprovalNeeded(runId, proposal);
        }
        else
        {
            await run.DisposeAsync();
        }
    }
}
