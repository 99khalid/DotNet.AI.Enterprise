using DotNet.AI.Enterprise.Application.Routing;

namespace DotNet.AI.Enterprise.Evaluation.Suites;

/// <summary>① Router: did each message (and each part of a multi-intent message) go to the right specialist?</summary>
public static class RouterSuite
{
    public static async Task<SuiteResult> RunAsync(IReadOnlyList<GoldenCase> cases, IntentRouter router, CancellationToken ct)
    {
        int correct = 0, usedLlm = 0;
        var failures = new List<string>();
        var confusion = new Dictionary<(Intent Expected, Intent Actual), int>();

        foreach (var c in cases)
        {
            var parts = await router.RouteAllAsync(c.Question, ct);
            var actual = parts.Select(p => p.Decision.Intent).ToArray();

            if (actual.SequenceEqual(c.Intents)) correct++;
            else failures.Add($"{c.Id}: expected [{string.Join(", ", c.Intents)}], got [{string.Join(", ", actual)}]" +
                              $" ({string.Join(", ", parts.Select(p => $"{p.Decision.Method} {p.Decision.Score}"))})");

            if (parts.Any(p => p.Decision.Method == "llm")) usedLlm++;

            if (c.SingleIntent is { } expected && actual.Length == 1)
                confusion[(expected, actual[0])] = confusion.GetValueOrDefault((expected, actual[0])) + 1;

            Progress.Tick(actual.SequenceEqual(c.Intents));
        }

        return new SuiteResult("Router",
        [
            Format.Rate("Accuracy", correct, cases.Count),
            new Metric("LLM fallback", (double)usedLlm / cases.Count, $"{100.0 * usedLlm / cases.Count:0}%  ({usedLlm}/{cases.Count})", IsQuality: false, IsRate: true)
        ], failures, ConfusionMatrix(confusion));
    }

    /// <summary>Rows = expected, columns = actual. Off-diagonal cells show which intents get mixed up.</summary>
    private static List<string> ConfusionMatrix(Dictionary<(Intent Expected, Intent Actual), int> counts)
    {
        var intents = Enum.GetValues<Intent>();
        var lines = new List<string> { "expected \\ actual".PadRight(18) + string.Concat(intents.Select(i => i.ToString().PadLeft(14))) };
        foreach (var e in intents)
            lines.Add(e.ToString().PadRight(18) + string.Concat(intents.Select(a => counts.GetValueOrDefault((e, a)).ToString().PadLeft(14))));
        return lines;
    }
}
