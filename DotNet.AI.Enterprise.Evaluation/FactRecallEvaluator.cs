using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace DotNet.AI.Enterprise.Evaluation;

/// <summary>The facts the answer must contain. "7|سبع" = either spelling counts.</summary>
public sealed class ExpectedFactsContext(IReadOnlyList<string> facts)
    : EvaluationContext("Expected facts", string.Join("\n", facts))
{
    public IReadOnlyList<string> Facts { get; } = facts;
}

/// <summary>
/// A custom evaluator — no LLM: checks in code that the answer contains the key facts ("15" days, "1500" EGP).
/// Cheap and repeatable, so it runs on every answer; the LLM judge is for what code can't check.
/// </summary>
public sealed class FactRecallEvaluator : IEvaluator
{
    public const string MetricName = "Fact recall";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var metric = new NumericMetric(MetricName);
        var expected = additionalContext?.OfType<ExpectedFactsContext>().FirstOrDefault();

        if (expected is null || expected.Facts.Count == 0)
        {
            metric.AddDiagnostics(EvaluationDiagnostic.Informational("No expected facts for this case."));
            return ValueTask.FromResult(new EvaluationResult(metric));
        }

        var answer = Normalize(modelResponse.Text);
        var missing = expected.Facts
            .Where(fact => !fact.Split('|').Any(alternative => answer.Contains(Normalize(alternative), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        metric.Value = (double)(expected.Facts.Count - missing.Count) / expected.Facts.Count;
        metric.Reason = missing.Count == 0 ? "All expected facts are in the answer." : $"Missing: {string.Join(", ", missing)}";
        metric.Interpretation = new EvaluationMetricInterpretation(
            missing.Count == 0 ? EvaluationRating.Good : EvaluationRating.Unacceptable, failed: missing.Count > 0, metric.Reason);

        return ValueTask.FromResult(new EvaluationResult(metric));
    }

    /// <summary>Arabic-Indic digits → 0-9, no diacritics, one spelling for أ/إ/آ, ة/ه, ى/ي.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue; // tashkeel
            sb.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                'أ' or 'إ' or 'آ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                _ => ch
            });
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
