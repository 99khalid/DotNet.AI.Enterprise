using DotNet.AI.Enterprise.Application.Leave;

namespace DotNet.AI.Enterprise.Evaluation.Suites;

/// <summary>④ Leave extraction: exact match per field. A field that isn't in the text must come back null.</summary>
public static class ExtractionSuite
{
    public static async Task<SuiteResult> RunAsync(IReadOnlyList<GoldenCase> cases, LeaveRequestExtractor extractor, CancellationToken ct)
    {
        int fields = 0, correctFields = 0, total = 0, exact = 0;
        var failures = new List<string>();

        foreach (var c in cases.Where(c => c.Leave is not null))
        {
            var expected = c.Leave!;
            var (draft, _) = await extractor.ExtractAsync(c.Question, ct);

            var checks = new (string Field, object? Expected, object? Actual)[]
            {
                ("Type", expected.Type, draft.Type),
                ("StartDate", expected.StartDate, draft.StartDate),
                ("EndDate", expected.EndDate, draft.EndDate),
            };
            var wrong = checks.Where(x => !Equals(x.Expected, x.Actual)).ToList();

            total++;
            fields += checks.Length;
            correctFields += checks.Length - wrong.Count;
            if (wrong.Count == 0) exact++;
            else failures.Add($"{c.Id}: " + string.Join(", ", wrong.Select(w => $"{w.Field} expected {w.Expected ?? "null"}, got {w.Actual ?? "null"}")));

            Progress.Tick(wrong.Count == 0);
        }

        return new SuiteResult("Extraction",
        [
            Format.Rate("Field accuracy", correctFields, fields),
            Format.Rate("Exact match", exact, total)
        ], failures);
    }
}
