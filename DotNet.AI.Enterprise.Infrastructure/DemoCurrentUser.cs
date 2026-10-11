using DotNet.AI.Enterprise.Application;

namespace DotNet.AI.Enterprise.Infrastructure;

/// <summary>Stand-in for real authentication (JWT / Entra ID) until the Web API stage.</summary>
public sealed class DemoCurrentUser : ICurrentUser
{
    public string EmployeeId => "E1001";
}
