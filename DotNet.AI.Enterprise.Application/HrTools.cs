using System.ComponentModel;
using DotNet.AI.Enterprise.Domain;
using Microsoft.Extensions.AI;

namespace DotNet.AI.Enterprise.Application;

/// <summary>
/// Functions the model may ASK to call. The model never runs them — FunctionInvokingChatClient
/// does, inside our process, with our permissions. The [Description] text is what the model reads
/// to decide when to call each tool, so it is effectively part of the prompt.
/// </summary>
public sealed class HrTools(IEmployeeRepository employees, ICurrentUser currentUser, TimeProvider time)
{
    public IList<AITool> AsAITools() =>
    [
        AIFunctionFactory.Create(GetCurrentDate),
        AIFunctionFactory.Create(GetMyProfile),
        AIFunctionFactory.Create(GetMyLeaveBalance),
        AIFunctionFactory.Create(GetPublicHolidays),
        AIFunctionFactory.Create(GetLeavePolicy),
    ];

    [Description("Returns today's date and day of week. Use it to resolve relative dates like 'tomorrow' or 'next Sunday'.")]
    public string GetCurrentDate()
    {
        var today = time.GetLocalNow();
        return $"{today:yyyy-MM-dd} ({today.DayOfWeek})";
    }

    [Description("Returns the current employee's profile: name, department, job title, manager and hire date.")]
    public object GetMyProfile()
    {
        var me = GetCurrentEmployee();
        var manager = me.ManagerId is null ? null : employees.GetById(me.ManagerId);

        return new
        {
            me.FullName,
            me.Department,
            me.JobTitle,
            ManagerName = manager?.FullName,
            HireDate = me.HireDate.ToString("yyyy-MM-dd")
        };
    }

    // No employeeId parameter on purpose: identity comes from ICurrentUser, not from the model.
    [Description("Returns the current employee's remaining leave balance in days. Only works for the logged-in employee.")]
    public object GetMyLeaveBalance()
    {
        var me = GetCurrentEmployee();
        return new { AnnualDays = me.AnnualLeaveBalance, SickDays = me.SickLeaveBalance };
    }
    [Description("Returns the list of public holidays for the current year.")]
    public object GetPublicHolidays()
    {
        // قائمة الإجازات الرسمية (يمكن جلبها من Database أو External API)
        var publicHolidays = new List<object>
    {
        new { Date = "2026-01-07", Name = "Coptic Christmas Day" },
        new { Date = "2026-01-25", Name = "January 25th Revolution / Police Day" },
        new { Date = "2026-03-21", Name = "Eid Al-Fitr" },
        new { Date = "2026-04-13", Name = "Sham El-Nessim" },
        new { Date = "2026-04-25", Name = "Sinai Liberation Day" },
        new { Date = "2026-05-01", Name = "Labor Day" },
        new { Date = "2026-05-27", Name = "Eid Al-Adha" },
        new { Date = "2026-06-18", Name = "Islamic New Year" },
        new { Date = "2026-06-30", Name = "June 30 Revolution" },
        new { Date = "2026-07-23", Name = "Revolution Day (July 23)" },
        new { Date = "2026-08-27", Name = "Prophet Muhammad's Birthday" },
        new { Date = "2026-10-06", Name = "Armed Forces Day" }
    };

        return new
        {
            Year = 2026,
            TotalHolidays = publicHolidays.Count,
            Holidays = publicHolidays
        };
    }

    // Hard-coded for now — in the RAG stage this comes from the company's real policy documents.
    [Description("Returns the company policy for a given leave type.")]
    public string GetLeavePolicy([Description("The leave type to look up.")] LeaveType type) => type switch
    {
        LeaveType.Annual => "21 days per year (30 after 10 years of service). Request at least 7 days in advance. Max 10 consecutive days without director approval.",
        LeaveType.Sick => "Up to 15 days per year. A medical certificate is required for more than 2 consecutive days.",
        LeaveType.Unpaid => "Up to 30 days per year, requires manager and HR approval.",
        _ => "No specific policy. Contact HR at hr@company.example."
    };

    private Employee GetCurrentEmployee() =>
        employees.GetById(currentUser.EmployeeId)
        ?? throw new InvalidOperationException($"Employee '{currentUser.EmployeeId}' not found.");
}
