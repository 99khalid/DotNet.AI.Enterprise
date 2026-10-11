using DotNet.AI.Enterprise.Application.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>
/// The "R" in RAG: question → vector → closest chunks → drop the weak ones.
/// Used by both modes: the SearchPolicies tool (Agentic) and HrAssistant (Always).
/// </summary>
public sealed class PolicyRetriever(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IPolicyStore store,
    IOptions<RagOptions> options,
    ILogger<PolicyRetriever> logger)
{
    public async Task<IReadOnlyList<PolicySearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var rag = options.Value;

        // Same embedding model as the documents — otherwise the vectors aren't comparable.
        var queryVector = await embedder.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        var candidates = await store.SearchAsync(queryVector, rag.TopK, cancellationToken);
        var relevant = candidates.Where(r => r.Score >= rag.MinScore).ToList();

        // Log ALL candidates with scores — this is how you tune MinScore and debug "why didn't it find X?".
        logger.LogInformation("Policy search \"{Query}\": kept {Kept}/{Total} (min {MinScore}) → {Results}",
            query, relevant.Count, candidates.Count, rag.MinScore,
            string.Join(" | ", candidates.Select(r => $"{r.Score:0.000} {r.Chunk.Citation}")));

        return relevant;
    }
}
