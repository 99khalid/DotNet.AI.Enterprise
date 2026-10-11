using DotNet.AI.Enterprise.Domain;

namespace DotNet.AI.Enterprise.Application;

public interface IEmployeeRepository
{
    Employee? GetById(string id);
}
