namespace DotNet.AI.Enterprise.Application;

/// <summary>
/// The authenticated user. Tools read identity from HERE — never from parameters the model sends —
/// so a prompt like "show my manager's balance" cannot reach someone else's data.
/// </summary>
public interface ICurrentUser
{
    string EmployeeId { get; }
}
