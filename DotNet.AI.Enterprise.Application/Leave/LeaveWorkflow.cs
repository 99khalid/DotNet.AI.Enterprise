using DotNet.AI.Enterprise.Application.Rag;
using DotNet.AI.Enterprise.Domain;
using Microsoft.Agents.AI.Workflows;

namespace DotNet.AI.Enterprise.Application.Leave;

// ---- Messages that travel between the steps ----

public sealed record ValidatedLeave(LeaveRequestDraft Draft, IReadOnlyList<string> Errors);

/// <summary>Everything the employee needs to see before approving — computed by code, not by the model.</summary>
public sealed record LeaveProposal(
    LeaveType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    int Days,
    string? Reason,
    int BalanceBefore,
    int? BalanceAfter,
    IReadOnlyList<string> HolidaysInRange,
    string? Policy);

public sealed record LeaveDecision(bool Approved, LeaveProposal Proposal);

public sealed record LeaveWorkflowResult(bool Submitted, string Message, string? RequestId, IReadOnlyList<string> Problems);

/// <summary>
/// Leave request as a WORKFLOW: the code fixes the path, the model does only the language step.
///
///   Extract (model) → Validate (code) ─┬─ errors ──→ NeedsInfo (output)
///                                      └─ valid ───→ Enrich (code) → ⏸ Approval (human) → Submit (code, output)
/// </summary>
public static class LeaveWorkflow
{
    public const string ApprovalPort = "Approval";

    /// <summary>A Workflow instance can serve one run at a time, so every run gets a fresh one.</summary>
    public static Workflow Build(LeaveRequestExtractor extractor, LeaveRequestService leave, PolicyRetriever retriever)
    {
        var extract = new ExtractStep(extractor);
        var validate = new ValidateStep(leave);
        var needsInfo = new NeedsInfoStep();
        var enrich = new EnrichStep(leave, retriever);
        var approval = RequestPort.Create<LeaveProposal, LeaveDecision>(ApprovalPort);
        var submit = new SubmitStep(leave);

        return new WorkflowBuilder(extract)
            .AddEdge(extract, validate)
            .AddEdge<ValidatedLeave>(validate, needsInfo, v => v!.Errors.Count > 0)
            .AddEdge<ValidatedLeave>(validate, enrich, v => v!.Errors.Count == 0)
            .AddEdge(enrich, approval)
            .AddEdge(approval, submit)
            .WithOutputFrom(needsInfo, submit)
            .WithName("LeaveRequest")
            .Build();
    }

    /// <summary>① The only step that needs the model: free text → structured draft.</summary>
    private sealed class ExtractStep(LeaveRequestExtractor extractor) : Executor<string, LeaveRequestDraft>("Extract")
    {
        public override async ValueTask<LeaveRequestDraft> HandleAsync(
            string message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            (await extractor.ExtractAsync(message, cancellationToken)).Draft;
    }

    /// <summary>② Business rules in code — deterministic and unit-testable.</summary>
    private sealed class ValidateStep(LeaveRequestService leave) : Executor<LeaveRequestDraft, ValidatedLeave>("Validate")
    {
        public override ValueTask<ValidatedLeave> HandleAsync(
            LeaveRequestDraft message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ValidatedLeave(message, leave.Validate(message)));
    }

    /// <summary>③a Something is missing or breaks a rule: stop and tell the employee what to fix.</summary>
    [YieldsOutput(typeof(LeaveWorkflowResult))]
    private sealed class NeedsInfoStep() : Executor<ValidatedLeave>("NeedsInfo")
    {
        public override ValueTask HandleAsync(
            ValidatedLeave message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            context.YieldOutputAsync(
                new LeaveWorkflowResult(false, "الطلب محتاج تعديل قبل ما يتقدّم:", null, message.Errors), cancellationToken);
    }

    /// <summary>③b Gather the facts for the approval card: balance, public holidays, the relevant policy.</summary>
    private sealed class EnrichStep(LeaveRequestService leave, PolicyRetriever retriever) : Executor<ValidatedLeave, LeaveProposal>("Enrich")
    {
        public override async ValueTask<LeaveProposal> HandleAsync(
            ValidatedLeave message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var draft = message.Draft;
            var me = leave.CurrentEmployee;
            var balance = draft.Type == LeaveType.Sick ? me.SickLeaveBalance : me.AnnualLeaveBalance;
            var countsAgainstBalance = draft.Type is LeaveType.Annual or LeaveType.Sick;

            var holidays = PublicHolidays.Between(draft.StartDate!.Value, draft.EndDate!.Value)
                .Select(h => $"{h.Date:yyyy-MM-dd} {h.Name}")
                .ToList();

            var policy = (await retriever.SearchAsync($"{draft.Type} leave policy", cancellationToken)).FirstOrDefault();

            return new LeaveProposal(
                draft.Type!.Value, draft.StartDate.Value, draft.EndDate.Value, draft.Days!.Value, draft.Reason,
                BalanceBefore: balance,
                BalanceAfter: countsAgainstBalance ? balance - draft.Days.Value : null,
                HolidaysInRange: holidays,
                Policy: policy?.Chunk.Citation);
        }
    }

    /// <summary>⑤ Runs only after the human decision arrives through the Approval port.</summary>
    [YieldsOutput(typeof(LeaveWorkflowResult))]
    private sealed class SubmitStep(LeaveRequestService leave) : Executor<LeaveDecision>("Submit")
    {
        public override async ValueTask HandleAsync(
            LeaveDecision message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            if (!message.Approved)
            {
                await context.YieldOutputAsync(new LeaveWorkflowResult(false, "تم إلغاء الطلب.", null, []), cancellationToken);
                return;
            }

            var p = message.Proposal;
            var (request, errors) = await leave.SubmitAsync(
                new LeaveRequestDraft(p.Type, p.StartDate, p.EndDate, p.Reason), cancellationToken);

            var result = request is null
                ? new LeaveWorkflowResult(false, "الطلب ماتقدّمش:", null, errors)
                : new LeaveWorkflowResult(true, $"✅ تم تقديم الطلب {request.Id} ({request.Days} أيام)، ومستني موافقة المدير.", request.Id, []);

            await context.YieldOutputAsync(result, cancellationToken);
        }
    }
}
