using System.ComponentModel;

namespace DotNet.AI.Enterprise.Domain;

/// <summary>
/// What the LLM extracts from free text. Every field is nullable on purpose:
/// if the information is not in the text, the model must return null instead of inventing it.
/// </summary>
[Description("A leave request extracted from an employee's message.")]
public sealed record LeaveRequestDraft(
    [property: Description("Type of leave. Null if not mentioned.")] LeaveType? Type,
    [property: Description("First day of leave (yyyy-MM-dd). Null if not mentioned.")] DateOnly? StartDate,
    [property: Description("Last day of leave, inclusive (yyyy-MM-dd). Null if not mentioned.")] DateOnly? EndDate,
    [property: Description("Reason in the employee's own words. Null if not mentioned.")] string? Reason)
{
    public const int MinAnnualNoticeDays = 7;

    public int? Days => StartDate is { } s && EndDate is { } e ? e.DayNumber - s.DayNumber + 1 : null;

    /// <summary>
    /// Structured Output guarantees the SHAPE, not the TRUTH — business rules are always checked in code.
    /// </summary>
    public IReadOnlyList<string> Validate(Employee employee, DateOnly today)
    {
        var errors = new List<string>();

        if (Type is null) errors.Add("نوع الإجازة مش واضح.");
        if (StartDate is null) errors.Add("تاريخ البداية مش موجود.");
        if (EndDate is null) errors.Add("تاريخ النهاية مش موجود.");

        if (StartDate is { } start && start < today)
            errors.Add("تاريخ البداية في الماضي.");

        // Company policy: annual leave must be requested at least 7 days in advance.
        if (Type == LeaveType.Annual && StartDate is { } annualStart && annualStart >= today)
        {
            var daysUntilStart = annualStart.DayNumber - today.DayNumber;
            if (daysUntilStart < MinAnnualNoticeDays)
                errors.Add($"الإجازة السنوية لازم تتطلب قبلها بـ {MinAnnualNoticeDays} أيام على الأقل (فاضل {daysUntilStart} يوم بس).");
        }

        if (Days is { } days)
        {
            if (days <= 0)
                errors.Add("تاريخ النهاية قبل تاريخ البداية.");
            else if (Type == LeaveType.Annual && days > employee.AnnualLeaveBalance)
                errors.Add($"المدة ({days} يوم) أكبر من رصيدك السنوي ({employee.AnnualLeaveBalance} يوم).");
            else if (Type == LeaveType.Sick && days > employee.SickLeaveBalance)
                errors.Add($"المدة ({days} يوم) أكبر من رصيدك المرضي ({employee.SickLeaveBalance} يوم).");
        }

        return errors;
    }
}
