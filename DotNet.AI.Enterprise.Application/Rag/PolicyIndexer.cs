using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>
/// Ingestion: documents → chunks → embeddings → store. Runs at startup.
/// Incremental: only new or changed chunks are embedded; chunks removed from the documents are deleted.
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
        var stored = await store.GetContentHashesAsync(cancellationToken);

        // Embedding calls cost time and money — skip every chunk whose text is unchanged.
        var changed = chunks
            .Where(c => !stored.TryGetValue(c.Id, out var hash) || hash != ContentHash(c))
            .ToList();
        var removed = stored.Keys.Except(chunks.Select(c => c.Id)).ToList();

        if (changed.Count > 0)
        {
            // ONE batched request for all changed chunks instead of one request per chunk.
            var embeddings = await embedder.GenerateAsync(changed.Select(c => c.EmbeddingText), cancellationToken: cancellationToken);

            for (var i = 0; i < changed.Count; i++)
                await store.UpsertAsync(changed[i], ContentHash(changed[i]), embeddings[i].Vector, cancellationToken);
        }

        if (removed.Count > 0)
            await store.DeleteAsync(removed, cancellationToken);

        logger.LogInformation(
            "Indexed {DocumentCount} policy documents, {ChunkCount} chunks: {Embedded} embedded, {Unchanged} unchanged, {Removed} removed",
            documents.Count, chunks.Count, changed.Count, chunks.Count - changed.Count, removed.Count);
    }

    /// <summary>Fingerprint of exactly what gets embedded: any change to the text (or header) changes the hash.</summary>
    private static string ContentHash(PolicyChunk chunk) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.EmbeddingText)));
}