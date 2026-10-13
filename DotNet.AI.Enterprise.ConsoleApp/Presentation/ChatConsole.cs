using System.Text;
using System.Text.Json;
using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Leave;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DotNet.AI.Enterprise.ConsoleApp.Presentation;

/// <summary>
/// Console UI only: reads input, prints output. All AI logic lives in HrAssistant and LeaveWorkflowService.
/// </summary>
public sealed class ChatConsole(HrAssistant assistant, LeaveWorkflowService workflows, IOptions<AiOptions> options)
{
    private const string Separator = "--------------------------------------------------";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var ai = options.Value;

        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.WriteLine("==================================================");
        Console.WriteLine($" 🧑‍💼 HR Assistant ({ai.Provider} / {ai.Model})");
        Console.WriteLine("==================================================");
        Console.WriteLine(" اكتب سؤالك عادي، أو استخدم الأوامر دي:");
        Console.WriteLine("   /leave <نص الطلب>   ← استخراج طلب إجازة (Structured Output)");
        Console.WriteLine("   /clear              ← مسح المحادثة");
        Console.WriteLine("   exit                ← خروج");

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("\n💬 > ");
            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("👋 Goodbye!");
                break;
            }

            try
            {
                if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    assistant.ClearHistory();
                    Console.WriteLine("🧹 المحادثة اتمسحت.");
                }
                else if (input.StartsWith("/leave ", StringComparison.OrdinalIgnoreCase))
                {
                    await ExtractLeaveAsync(input["/leave ".Length..], cancellationToken);
                }
                else
                {
                    await ChatAsync(input, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"\n⏱️ Request timed out after {ai.TimeoutSeconds}s. Try again.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"\n❌ {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task ChatAsync(string prompt, CancellationToken cancellationToken)
    {
        // 1. The router picks the specialist — one per request when the message holds several.
        var parts = await assistant.RouteAllAsync(prompt, cancellationToken);

        foreach (var part in parts)
        {
            var route = part.Decision;
            Console.WriteLine($"\n🔀 [Route] {route.Intent} ({route.Method}, {route.Score})" +
                              (parts.Count > 1 ? $" ← \"{part.Text}\"" : ""));

            // 2a. Leave requests always run as the workflow.
            if (route.Intent == Intent.LeaveRequest)
                await RunLeaveWorkflowAsync(workflows.StartAsync(part.Text, cancellationToken), cancellationToken);
            // 2b. Everything else: the chosen specialist agent answers.
            else
                await AnswerAsync(part.Text, route.Intent, cancellationToken);
        }
    }

    private async Task AnswerAsync(string prompt, Intent intent, CancellationToken cancellationToken)
    {
        Console.WriteLine($"🤖 [HR Assistant]:\n{Separator}");

        await foreach (var update in assistant.AskStreamingAsync(prompt, intent, cancellationToken))
        {
            // Make tool calling visible — this is what happens "behind the scenes".
            foreach (var call in update.Contents.OfType<FunctionCallContent>())
                Console.WriteLine($"🔧 [Tool call] {call.Name}({FormatArgs(call.Arguments)})");

            Console.Write(update.Text);
        }

        Console.WriteLine($"\n{Separator}");
        foreach (var source in assistant.LastSources)
            Console.WriteLine($"📄 {source.Chunk.Citation} ({source.Score:0.000})");
        PrintUsage();
    }

    /// <summary>Prints the workflow's steps; when it pauses for approval, asks y/n and resumes it.</summary>
    private async Task RunLeaveWorkflowAsync(IAsyncEnumerable<LeaveWorkflowUpdate> updates, CancellationToken cancellationToken)
    {
        LeaveApprovalNeeded? pending = null;

        await foreach (var update in updates)
        {
            switch (update)
            {
                case LeaveStepCompleted step:
                    Console.WriteLine($"   ✔ {step.Step}");
                    break;
                case LeaveApprovalNeeded approval:
                    pending = approval;
                    break;
                case LeaveWorkflowFinished { Result: var result }:
                    Console.WriteLine($"\n{(result.Submitted ? "✅" : "⚠️")} {result.Message}");
                    foreach (var problem in result.Problems) Console.WriteLine($"   • {problem}");
                    break;
            }
        }

        if (pending is null) return;

        var p = pending.Proposal;
        Console.WriteLine($"\n⏸️  [Review] {p.Type} {p.StartDate:yyyy-MM-dd} → {p.EndDate:yyyy-MM-dd} ({p.Days} days)" +
                          $" | reason: {p.Reason ?? "—"} | balance: {p.BalanceBefore} → {p.BalanceAfter?.ToString() ?? "—"}" +
                          $" | holidays: {(p.HolidaysInRange.Count == 0 ? "none" : string.Join(", ", p.HolidaysInRange))}");
        Console.Write("   Submit? (y/n): ");
        var approved = Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;

        await RunLeaveWorkflowAsync(workflows.ResumeAsync(pending.RunId, approved, cancellationToken), cancellationToken);
    }

    private async Task ExtractLeaveAsync(string text, CancellationToken cancellationToken)
    {
        Console.WriteLine("\n📋 [Extracted leave request]:");
        Console.WriteLine(Separator);

        var (draft, errors) = await assistant.ExtractLeaveRequestAsync(text, cancellationToken);

        if (draft is not null)
        {
            Console.WriteLine($"  Type      : {draft.Type?.ToString() ?? "—"}");
            Console.WriteLine($"  From      : {draft.StartDate?.ToString("yyyy-MM-dd") ?? "—"}");
            Console.WriteLine($"  To        : {draft.EndDate?.ToString("yyyy-MM-dd") ?? "—"}");
            Console.WriteLine($"  Days      : {draft.Days?.ToString() ?? "—"}");
            Console.WriteLine($"  Reason    : {draft.Reason ?? "—"}");
        }

        Console.WriteLine(Separator);
        if (errors.Count == 0)
            Console.WriteLine("✅ الطلب سليم وجاهز للإرسال.");
        else
            foreach (var error in errors) Console.WriteLine($"⚠️ {error}");

        PrintUsage();
    }

    private void PrintUsage()
    {
        var ai = options.Value;
        var last = assistant.LastUsage;
        var session = assistant.SessionUsage;

        var line = $"📊 Tokens — in: {last?.InputTokenCount ?? 0} | out: {last?.OutputTokenCount ?? 0}" +
                   $"   ‖ session total: {session.TotalTokenCount ?? 0}" +
                   $"   ‖ history: {assistant.HistoryCount} msgs";

        if (ai.InputCostPerMillionTokens is { } inPrice && ai.OutputCostPerMillionTokens is { } outPrice)
        {
            var cost = (session.InputTokenCount ?? 0) * inPrice / 1_000_000m
                     + (session.OutputTokenCount ?? 0) * outPrice / 1_000_000m;
            line += $"   ‖ session cost: ${cost:0.00000}";
        }

        Console.WriteLine(line);
    }

    private static string FormatArgs(IDictionary<string, object?>? args) =>
        args is null || args.Count == 0 ? "" : JsonSerializer.Serialize(args);
}
