using Microsoft.Extensions.AI;

namespace DotNet.AI.Enterprise.WebApi.Endpoints;

// ---- Requests ----

public sealed record SendMessageRequest(string Message);

public sealed record StartLeaveWorkflowRequest(string Text);

public sealed record ApprovalDecisionRequest(bool Approved);

// ---- Responses ----

public sealed record InfoResponse(string Provider, string Model, string EmployeeName);

public sealed record UsageDto(long? InputTokens, long? OutputTokens, long? TotalTokens, long? SessionTotalTokens, int HistoryCount)
{
    public static UsageDto From(UsageDetails? last, UsageDetails session, int historyCount) =>
        new(last?.InputTokenCount, last?.OutputTokenCount, last?.TotalTokenCount, session.TotalTokenCount, historyCount);
}

// ---- Server-Sent Event payloads (one event per item while the answer streams) ----

public sealed record ToolCallEvent(string Name, IDictionary<string, object?>? Arguments);

public sealed record TextEvent(string Delta);

public sealed record ErrorEvent(string Message);

/// <summary>The agent paused before a tool that needs the employee's approval.</summary>
public sealed record ApprovalEvent(string RequestId, string ToolName, IDictionary<string, object?>? Arguments);

public sealed record SourceDto(string Citation, double Score);

public sealed record SourcesEvent(IReadOnlyList<SourceDto> Sources);
