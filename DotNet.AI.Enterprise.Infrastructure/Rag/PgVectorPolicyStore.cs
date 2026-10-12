using System.Text.RegularExpressions;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Rag;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;

namespace DotNet.AI.Enterprise.Infrastructure.Rag;

/// <summary>
/// IPolicyStore on PostgreSQL + pgvector, through the Microsoft.Extensions.VectorData abstraction.
/// Vectors survive restarts, so only changed chunks need new embeddings.
/// </summary>
public sealed partial class PgVectorPolicyStore : IPolicyStore
{
    private const int MaxChunks = 100_000;

    private readonly VectorStoreCollection<string, PolicyChunkRecord> _collection;
    private readonly int _dimensions;
    private bool _created;

    public PgVectorPolicyStore(VectorStore vectorStore, IOptions<EmbeddingOptions> embeddingOptions)
    {
        var embedding = embeddingOptions.Value;
        _dimensions = embedding.Dimensions ?? throw new InvalidOperationException(
            "Embedding:Dimensions is required when Rag:Store is Postgres (e.g. 1024 for bge-m3, 1536 for gemini-embedding-001).");

        // One table per embedding model + size, e.g. "policy_chunks_bge_m3_1024".
        // Vectors from different models are never mixed, and switching models starts a fresh table (= full re-index).
        var table = $"policy_chunks_{NonAlphanumeric().Replace(embedding.Model.ToLowerInvariant(), "_")}_{_dimensions}";
        _collection = vectorStore.GetCollection<string, PolicyChunkRecord>(table, Definition(_dimensions));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken);

        var hashes = new Dictionary<string, string>();
        await foreach (var record in _collection.GetAsync(r => true, MaxChunks, cancellationToken: cancellationToken))
            hashes[record.Id] = record.ContentHash;

        return hashes;
    }

    public async Task UpsertAsync(PolicyChunk chunk, string contentHash, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default)
    {
        // Fail fast with a clear message instead of a cryptic database error.
        if (vector.Length != _dimensions)
            throw new InvalidOperationException(
                $"The embedding model returned {vector.Length} dimensions but Embedding:Dimensions is {_dimensions}.");

        await EnsureCreatedAsync(cancellationToken);
        await _collection.UpsertAsync(PolicyChunkRecord.From(chunk, contentHash, vector), cancellationToken);
    }

    public async Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken);
        await _collection.DeleteAsync(ids, cancellationToken);
    }

    public async Task<IReadOnlyList<PolicySearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector, int top, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken);

        // The database ranks by similarity: ORDER BY embedding <=> @query LIMIT @top
        var results = new List<PolicySearchResult>();
        await foreach (var hit in _collection.SearchAsync(queryVector, top, cancellationToken: cancellationToken))
            results.Add(new PolicySearchResult(hit.Record.ToChunk(), (float)(hit.Score ?? 0)));

        return results;
    }

    // CREATE TABLE IF NOT EXISTS — safe to call more than once.
    private async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_created) return;
        await _collection.EnsureCollectionExistsAsync(cancellationToken);
        _created = true;
    }

    private static VectorStoreCollectionDefinition Definition(int dimensions) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(PolicyChunkRecord.Id), typeof(string)),
            new VectorStoreDataProperty(nameof(PolicyChunkRecord.Document), typeof(string)),
            new VectorStoreDataProperty(nameof(PolicyChunkRecord.Section), typeof(string)),
            new VectorStoreDataProperty(nameof(PolicyChunkRecord.Text), typeof(string)),
            new VectorStoreDataProperty(nameof(PolicyChunkRecord.ContentHash), typeof(string)),
            new VectorStoreVectorProperty(nameof(PolicyChunkRecord.Embedding), typeof(ReadOnlyMemory<float>), dimensions)
            {
                // Score comes back as similarity (1 = same meaning), same as InMemoryPolicyStore.
                DistanceFunction = DistanceFunction.CosineSimilarity,
                // pgvector's HNSW index supports at most 2000 dimensions; above that, use exact (Flat) search.
                IndexKind = dimensions <= 2000 ? IndexKind.Hnsw : IndexKind.Flat
            }
        ]
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}