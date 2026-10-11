using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Domain;

namespace DotNet.AI.Enterprise.Infrastructure;

/// <summary>Demo data. Will be replaced by a real database in a later stage.</summary>
public sealed class InMemoryEmployeeRepository : IEmployeeRepository
{
    private static readonly Dictionary<string, Employee> Employees = new[]
    {
        new Employee("E1001", "أحمد علي", "Engineering", "Software Engineer", "E1002", new DateOnly(2023, 3, 1), 14, 7),
        new Employee("E1002", "سارة محمود", "Engineering", "Engineering Manager", null, new DateOnly(2019, 6, 15), 25, 10),
    }.ToDictionary(e => e.Id);

    public Employee? GetById(string id) => Employees.GetValueOrDefault(id);
}
