using System.Diagnostics;
using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace DotNet.AI.Enterprise.Evaluation.Suites;

/// <summary>
/// ③ Tools + ⑤ answers: each question goes to its specialist agent (a fresh HrAssistant, no history), then
///   - tool selection is checked in code (did the Profile agent call the expected tool?),
///   - fact recall is checked in code (FactRecallEvaluator),
///   - groundedness is graded by the judge model (GroundednessEvaluator), only when the judge is on.
/// Every scored answer is stored by the reporting library, so `dotnet aieval report` can render it.
/// </summary>
public static class AgentSuite
{
    private const double MinGroundedness = 4; // 1–5 scale

    public static async Task<SuiteResult> RunAsync(
        IReadOnlyList<GoldenCase> cases,
        IServiceProvider services,
        ReportingConfiguration factsOnly,
        ReportingConfiguration? judged,
        CancellationToken ct)
    {
        int toolCases = 0, toolCorrect = 0, factCases = 0, allFacts = 0, judgeErrors = 0;
        double factRecallSum = 0;
        var groundedness = new List<double>();
        var latency = new List<double>();
        var failures = new List<string>();

        foreach (var c in cases.Where(c => c.SingleIntent is Intent.Policy or Intent.Profile))
        {
            var intent = c.SingleIntent!.Value;
            var assistant = services.GetRequiredService<HrAssistant>(); // transient: a fresh conversation per case

            var stopwatch = Stopwatch.StartNew();
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in assistant.AskStreamingAsync(c.Question, intent, ct))
                updates.Add(update);
            latency.Add(stopwatch.Elapsed.TotalSeconds);

            var response = updates.ToChatResponse();
            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(f => f.Name).ToList();
            var sources = assistant.LastSources;
            var passed = true;

            // ③ Tool selection ("none" = the question must not reach any tool).
            if (c.Tool is not null)
            {
                toolCases++;
                var ok = c.Tool == "none" ? calls.Count == 0 : calls.Contains(c.Tool);
                if (ok) toolCorrect++;
                else
                {
                    passed = false;
                    failures.Add($"{c.Id}: expected tool {c.Tool}, called [{string.Join(", ", calls)}]");
                }
            }

            // ⑤ Answer: code checks always; the judge only when there are excerpts to be grounded in.
            List<EvaluationContext> context = [];
            if (c.Facts is { Length: > 0 }) context.Add(new ExpectedFactsContext(c.Facts));

            // Judge only answerable policy questions: unanswerable ones are covered by the code checks,
            // and every judge call counts against the provider's quota.
            var useJudge = judged is not null && c.Source is not null && sources.Count > 0;
            if (useJudge)
                context.Add(new GroundednessEvaluatorContext(
                    string.Join("\n\n", sources.Select(s => $"[{s.Chunk.Citation}]\n{s.Chunk.Text}"))));

            var reporting = useJudge ? judged! : factsOnly;
            await using var scenario = await reporting.CreateScenarioRunAsync(c.Id, cancellationToken: ct);
            var result = await scenario.EvaluateAsync([new ChatMessage(ChatRole.User, c.Question)], response, context, ct);
            var answer = response.Text.ReplaceLineEndings(" ").Trim();

            if (c.Facts is { Length: > 0 })
            {
                var recall = result.Get<NumericMetric>(FactRecallEvaluator.MetricName);
                factCases++;
                factRecallSum += recall.Value ?? 0;
                if (recall.Value == 1) allFacts++;
                else
                {
                    passed = false;
                    failures.Add($"{c.Id}: {recall.Reason} — answer: \"{Shorten(answer)}\"");
                }
            }

            if (useJudge)
            {
                // On a judge error the metric has no value (not even a NumericMetric) — only diagnostics.
                result.Metrics.TryGetValue(GroundednessEvaluator.GroundednessMetricName, out var metric);
                var grounded = metric as NumericMetric;
                if (grounded?.Value is { } score)
                {
                    groundedness.Add(score);
                    if (score < MinGroundedness)
                    {
                        passed = false;
                        failures.Add($"{c.Id}: groundedness {score}/5 — {Shorten(grounded.Reason ?? "")} — answer: \"{Shorten(answer)}\"");
                    }
                }
                else
                {
                    judgeErrors++;
                    var error = metric?.Diagnostics?.FirstOrDefault()?.Message.Split('\n')[0].Trim() ?? "no metric returned";
                    failures.Add($"{c.Id}: judge gave no score — {Shorten(error)}");
                }
            }

            Progress.Tick(passed);
        }

        List<Metric> metrics =
        [
            Format.Rate("Tool accuracy", toolCorrect, toolCases),
            new("Fact recall", factCases == 0 ? 0 : factRecallSum / factCases, $"{(factCases == 0 ? 0 : 100 * factRecallSum / factCases):0}%  (all facts in {allFacts}/{factCases})", IsRate: true,
                // Even at temperature 0 a local model is not fully deterministic: allow a one-case swing.
                Tolerance: factCases == 0 ? 0 : 1.0 / factCases + 0.0001),
        ];

        // A partial judge run (quota, overload) is shown but never becomes or breaks the baseline.
        if (groundedness.Count > 0)
            metrics.Add(judgeErrors == 0
                ? new Metric("Groundedness", groundedness.Average(), $"{groundedness.Average():0.00} / 5  (n={groundedness.Count})", Tolerance: 0.25)
                : new Metric("Groundedness (partial)", groundedness.Average(),
                    $"{groundedness.Average():0.00} / 5  (n={groundedness.Count}, {judgeErrors} judge errors)", IsQuality: false));

        metrics.Add(new Metric("Avg answer time", latency.Average(), $"{latency.Average():0.0}s", IsQuality: false));

        return new SuiteResult("Agents", metrics, failures);
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "…";
}
