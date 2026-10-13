using System.Text;
using System.Text.Json;
using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DotNet.AI.Enterprise.ConsoleApp.Presentation;

/// <summary>
/// Console UI only: reads input, prints output. All AI logic lives in HrAssistant.
/// </summary>
public sealed class ChatConsole(HrAssistant assistant, IOptions<AiOptions> options)
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
        var pending = await PrintReplyAsync(assistant.AskStreamingAsync(prompt, cancellationToken));

        // Human-in-the-loop: the agent paused before a tool that changes data. Ask the employee for each one.
        while (pending.Count > 0)
        {
            var approval = pending[0];
            var call = (FunctionCallContent)approval.ToolCall;

            Console.WriteLine($"\n⏸️  [Needs your approval] {call.Name}({FormatArgs(call.Arguments)})");
            Console.Write("   Approve? (y/n): ");
            var approved = Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;

            pending = await PrintReplyAsync(assistant.RespondToApprovalAsync(approval.RequestId, approved, cancellationToken));
        }
    }

    /// <summary>Prints a streamed reply and returns any approval requests the agent paused for.</summary>
    private async Task<List<ToolApprovalRequestContent>> PrintReplyAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var approvals = new List<ToolApprovalRequestContent>();
        Console.WriteLine($"\n🤖 [HR Assistant]:\n{Separator}");

        await foreach (var update in updates)
        {
            // Make tool calling visible — this is what happens "behind the scenes".
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent call)
                    Console.WriteLine($"🔧 [Tool call] {call.Name}({FormatArgs(call.Arguments)})");
                else if (content is ToolApprovalRequestContent approval)
                    approvals.Add(approval);
            }

            Console.Write(update.Text);
        }

        Console.WriteLine($"\n{Separator}");
        PrintUsage();
        return approvals;
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
