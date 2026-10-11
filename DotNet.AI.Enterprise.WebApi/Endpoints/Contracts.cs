using DotNet.AI.Enterprise.Domain;
using Microsoft.Extensions.AI;

namespace DotNet.AI.Enterprise.WebApi.Endpoints;

// ---- Requests ----

public sealed record SendMessageRequest(string Message);

public sealed record ExtractLeaveRequest(string Text);

// ---- Responses ----

public sealed record InfoResponse(string Provider, string Model, string EmployeeName);

public sealed record UsageDto(long? InputTokens, long? OutputTokens, long? TotalTokens, long? SessionTotalTokens, int HistoryCount)
{
    public static UsageDto From(UsageDetails? last, UsageDetails session, int historyCount) =>
        new(last?.InputTokenCount, last?.OutputTokenCount, last?.TotalTokenCount, session.TotalTokenCount, historyCount);
}

public sealed record ExtractLeaveResponse(LeaveRequestDraft? Draft, IReadOnlyList<string> Errors, UsageDto Usage); // Draft includes the computed Days

// ---- Server-Sent Event payloads (one event per item while the answer streams) ----

public sealed record ToolCallEvent(string Name, IDictionary<string, object?>? Arguments);

public sealed record TextEvent(string Delta);

public sealed record ErrorEvent(string Message);
