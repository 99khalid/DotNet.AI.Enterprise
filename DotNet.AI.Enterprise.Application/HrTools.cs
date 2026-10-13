using DotNet.AI.Enterprise.Application.Rag;
using DotNet.AI.Enterprise.Domain;
using Microsoft.Extensions.AI;
using System.ComponentModel;

namespace DotNet.AI.Enterprise.Application;

/// <summary>
/// Functions the model may ASK to call. The model never runs them — FunctionInvokingChatClient
/// does, inside our process, with our permissions. The [Description] text is what the model reads
/// to decide when to call each tool, so it is effectively part of the prompt.
/// </summary>
public sealed class HrTools(
    IEmployeeRepository employees,
    ICurrentUser currentUser,
    TimeProvider time,
    PolicyRetriever retriever,
    ILeaveRequestRepository leaveRequests)
{
    public IList<AITool> AsAITools(bool includePolicySearch = true)
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(GetCurrentDate),
            AIFunctionFactory.Create(GetMyProfile),
            AIFunctionFactory.Create(GetMyLeaveBalance),
            AIFunctionFactory.Create(GetPublicHolidays),
            // Changes data → the employee must approve every call before it runs (human-in-the-loop).
#pragma warning disable MEAI001 // ApprovalRequiredAIFunction is still marked experimental
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(SubmitLeaveRequest)),
#pragma warning restore MEAI001
        ];

        if (includePolicySearch)
            tools.Add(AIFunctionFactory.Create(SearchPolicies));

        return tools;
    }

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
    // The Arabic examples help small models map colloquial words ("عطلة", "فاضلي") to this tool.
    [Description("Returns the current employee's remaining leave balance in days (annual and sick). " +
                 "Use it for questions like: رصيدي كام، فاضلي كام يوم إجازة، عندي كام يوم عطلة، كام يوم أجازة باقيلي.")]
    public object GetMyLeaveBalance()
    {
        var me = GetCurrentEmployee();
        return new { AnnualDays = me.AnnualLeaveBalance, SickDays = me.SickLeaveBalance };
    }

    // RAG: the policy text now comes from documents, not from code.
    [Description("Searches the company's HR policy documents (leave, attendance, remote work, permissions...). " +
                 "Use it for ANY question about rules or policies. Returns the most relevant excerpts with their source.")]
    public async Task<object> SearchPolicies(
        [Description("What to search for, as a short phrase in the employee's language.")] string query,
        CancellationToken cancellationToken)
    {
        var results = await retriever.SearchAsync(query, cancellationToken);

        if (results.Count == 0)
            return "No relevant policy was found for this question.";

        return results
            .Select(r => new { Source = r.Chunk.Citation, r.Chunk.Text, Score = Math.Round(r.Score, 3) })
            .ToList();
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

    [Description("Submits a leave request for the current employee to their manager. " +
                 "The employee confirms it in the app before it is actually submitted, so call it directly — don't ask for confirmation in text. " +
                 "Use GetCurrentDate first to turn relative dates into exact ones.")]
    public async Task<object> SubmitLeaveRequest(
        [Description("Type of leave.")] LeaveType type,
        [Description("First day of leave, yyyy-MM-dd.")] DateOnly startDate,
        [Description("Last day of leave (inclusive), yyyy-MM-dd.")] DateOnly endDate,
        [Description("Reason in the employee's words. Optional.")] string? reason,
        CancellationToken cancellationToken)
    {
        var me = GetCurrentEmployee();
        var today = DateOnly.FromDateTime(time.GetLocalNow().Date);

        // Approval means "I want this", not "this is valid": the business rules are checked again
        // at execution time (the balance may have changed since the request was proposed).
        var draft = new LeaveRequestDraft(type, startDate, endDate, reason);
        var errors = draft.Validate(me, today);
        if (errors.Count > 0)
            return new { Submitted = false, Errors = errors };

        var request = new LeaveRequest(
            Id: $"LR-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            EmployeeId: me.Id,
            Type: type,
            StartDate: startDate,
            EndDate: endDate,
            Days: draft.Days!.Value,
            Reason: reason,
            Status: LeaveRequestStatus.PendingManagerApproval,
            SubmittedAt: time.GetUtcNow());

        await leaveRequests.AddAsync(request, cancellationToken);

        return new { Submitted = true, request.Id, request.Days, Status = "Waiting for manager approval" };
    }

    private Employee GetCurrentEmployee() =>
        employees.GetById(currentUser.EmployeeId)
        ?? throw new InvalidOperationException($"Employee '{currentUser.EmployeeId}' not found.");
}
