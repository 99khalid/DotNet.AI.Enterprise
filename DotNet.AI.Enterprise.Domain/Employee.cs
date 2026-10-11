namespace DotNet.AI.Enterprise.Domain;

public sealed record Employee(
    string Id,
    string FullName,
    string Department,
    string JobTitle,
    string? ManagerId,
    DateOnly HireDate,
    int AnnualLeaveBalance,
    int SickLeaveBalance);
