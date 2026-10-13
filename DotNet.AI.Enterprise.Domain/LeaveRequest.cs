namespace DotNet.AI.Enterprise.Domain;

/// <summary>A leave request the employee confirmed and submitted. Waits for the manager's decision.</summary>
public sealed record LeaveRequest(
    string Id,
    string EmployeeId,
    LeaveType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    int Days,
    string? Reason,
    LeaveRequestStatus Status,
    DateTimeOffset SubmittedAt);

public enum LeaveRequestStatus
{
    PendingManagerApproval
}
