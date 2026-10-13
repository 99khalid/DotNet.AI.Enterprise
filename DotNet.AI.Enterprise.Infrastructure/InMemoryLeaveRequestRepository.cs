using System.Collections.Concurrent;
using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Domain;

namespace DotNet.AI.Enterprise.Infrastructure;

/// <summary>Demo storage for submitted requests. Will move to a real database in the Production stage.</summary>
public sealed class InMemoryLeaveRequestRepository : ILeaveRequestRepository
{
    private readonly ConcurrentDictionary<string, LeaveRequest> _requests = new();

    public Task AddAsync(LeaveRequest request, CancellationToken cancellationToken = default)
    {
        _requests[request.Id] = request;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LeaveRequest>> GetByEmployeeAsync(string employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LeaveRequest>>(
            _requests.Values.Where(r => r.EmployeeId == employeeId).OrderByDescending(r => r.SubmittedAt).ToList());
}
