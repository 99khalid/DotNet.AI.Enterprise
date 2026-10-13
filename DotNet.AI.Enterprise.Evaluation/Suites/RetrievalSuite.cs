using DotNet.AI.Enterprise.Application.Rag;

namespace DotNet.AI.Enterprise.Evaluation.Suites;

/// <summary>
/// ② Retrieval, no LLM involved: is the right article among the results (Hit@K), how high (MRR),
/// and do questions the policies don't cover get NO results (so the answer can say "not covered")?
/// </summary>
public static class RetrievalSuite
{
    public static async Task<SuiteResult> RunAsync(IReadOnlyList<GoldenCase> cases, PolicyRetriever retriever, int topK, CancellationToken ct)
    {
        int answerable = 0, hits = 0, unanswerable = 0, rejected = 0;
        double reciprocalRanks = 0;
        var failures = new List<string>();

        foreach (var c in cases.Where(c => c.Source is not null || c.Unanswerable))
        {
            var results = await retriever.SearchAsync(c.Question, ct);
            var found = string.Join(" | ", results.Select(r => $"{r.Chunk.Citation} {r.Score:0.000}"));

            if (c.Unanswerable)
            {
                unanswerable++;
                var ok = results.Count == 0;
                if (ok) rejected++;
                else failures.Add($"{c.Id}: should find nothing, got {found}");
                Progress.Tick(ok);
                continue;
            }

            answerable++;
            var rank = results.ToList().FindIndex(r => r.Chunk.Citation == c.Source) + 1; // 0 = not found
            var hit = rank > 0;
            if (hit)
            {
                hits++;
                reciprocalRanks += 1.0 / rank;
            }

            if (!hit || rank > 1)
                failures.Add($"{c.Id}: expected \"{c.Source}\" {(hit ? $"at rank {rank}" : "MISSING")} — got {(found == "" ? "nothing" : found)}");
            Progress.Tick(hit);
        }

        var mrr = answerable == 0 ? 0 : reciprocalRanks / answerable;
        return new SuiteResult("Retrieval",
        [
            Format.Rate($"Hit@{topK}", hits, answerable),
            new Metric("MRR", mrr, $"{mrr:0.000}"),
            Format.Rate("No-answer rejected", rejected, unanswerable)
        ], failures);
    }
}
