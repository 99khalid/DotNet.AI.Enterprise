using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>
/// Ingestion: documents → chunks → embeddings → store. Runs once at startup.
/// </summary>
public sealed class PolicyIndexer(
    IPolicyDocumentSource source,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IPolicyStore store,
    ILogger<PolicyIndexer> logger)
{
    public async Task IndexAsync(CancellationToken cancellationToken = default)
    {
        var documents = await source.LoadAsync(cancellationToken);
        var chunks = documents.SelectMany(MarkdownChunker.Chunk).ToList();

        // ONE batched request for all chunks instead of one request per chunk:
        // fewer calls = less quota used and much faster startup.
        var embeddings = await embedder.GenerateAsync(chunks.Select(c => c.EmbeddingText), cancellationToken: cancellationToken);

        for (var i = 0; i < chunks.Count; i++)
            await store.UpsertAsync(chunks[i], embeddings[i].Vector, cancellationToken);

        logger.LogInformation("Indexed {ChunkCount} chunks from {DocumentCount} policy documents", chunks.Count, documents.Count);
    }
}