using DotNet.AI.Enterprise.Domain;

namespace DotNet.AI.Enterprise.Application.Leave;

/// <summary>
/// Submitting a leave request — shared by the agent's SubmitLeaveRequest tool and the leave workflow,
/// so the rules live in one place.
/// </summary>
public sealed class LeaveRequestService(
    IEmployeeRepository employees,
    ICurrentUser currentUser,
    ILeaveRequestRepository leaveRequests,
    TimeProvider time)
{
    public Employee CurrentEmployee =>
        employees.GetById(currentUser.EmployeeId)
        ?? throw new InvalidOperationException($"Employee '{currentUser.EmployeeId}' not found.");

    public DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    public IReadOnlyList<string> Validate(LeaveRequestDraft draft) => draft.Validate(CurrentEmployee, Today);

    /// <summary>
    /// Re-validates and saves. Approval means "I want this", not "this is valid": the rules are checked
    /// again at execution time (the balance may have changed since the request was proposed).
    /// </summary>
    public async Task<(LeaveRequest? Request, IReadOnlyList<string> Errors)> SubmitAsync(
        LeaveRequestDraft draft, CancellationToken cancellationToken = default)
    {
        var errors = Validate(draft);
        if (errors.Count > 0)
            return (null, errors);

        var request = new LeaveRequest(
            Id: $"LR-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            EmployeeId: CurrentEmployee.Id,
            Type: draft.Type!.Value,
            StartDate: draft.StartDate!.Value,
            EndDate: draft.EndDate!.Value,
            Days: draft.Days!.Value,
            Reason: draft.Reason,
            Status: LeaveRequestStatus.PendingManagerApproval,
            SubmittedAt: time.GetUtcNow());

        await leaveRequests.AddAsync(request, cancellationToken);
        return (request, []);
    }
}
