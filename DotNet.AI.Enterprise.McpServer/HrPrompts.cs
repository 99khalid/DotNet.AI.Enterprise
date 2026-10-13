using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DotNet.AI.Enterprise.McpServer;

/// <summary>
/// Prompts = ready-made templates the USER picks (shown as slash commands in hosts like VS Code).
/// They only produce text for the host's model — the tools still do the work.
/// </summary>
[McpServerPromptType]
public sealed class HrPrompts
{
    [McpServerPrompt(Name = "plan_leave")]
    [Description("Checks a planned leave against your balance, public holidays and the leave policy — without submitting anything.")]
    public static string PlanLeave(
        [Description("First day, yyyy-MM-dd")] string startDate,
        [Description("Last day, yyyy-MM-dd")] string endDate,
        [Description("Annual, Sick, Unpaid... (optional)")] string? type = null,
        [Description("Reason (optional)")] string? reason = null) =>
        $"""
        I'm planning a leave ({type ?? "Annual"}) from {startDate} to {endDate}{(reason is null ? "" : $" because: {reason}")}.
        1. Call get_my_leave_balance and tell me my balance before and after this leave.
        2. Call get_public_holidays and tell me if any public holiday falls in this range.
        3. Call search_policies for the rules of this leave type (notice period, documents, limits) and cite the source.
        Then give me a short verdict: OK to submit, or what to change. Do not submit anything.
        Reply in the language of this request.
        """;
}
