using DotNet.AI.Enterprise.Domain;

namespace DotNet.AI.Enterprise.Application;

public interface ILeaveRequestRepository
{
    Task AddAsync(LeaveRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LeaveRequest>> GetByEmployeeAsync(string employeeId, CancellationToken cancellationToken = default);
}
