using System.Collections.Concurrent;
using System.Numerics.Tensors;
using DotNet.AI.Enterprise.Application.Rag;

namespace DotNet.AI.Enterprise.Infrastructure.Rag;

/// <summary>
/// A vector store in ~20 lines: keep every vector in memory and compare the query against ALL of them.
/// Fine for hundreds of chunks. Real vector databases use an index (e.g. HNSW) to avoid
/// comparing against everything when there are millions.
/// </summary>
public sealed class InMemoryPolicyStore : IPolicyStore
{
    private readonly ConcurrentDictionary<string, (PolicyChunk Chunk, ReadOnlyMemory<float> Vector)> _items = new();

    public int Count => _items.Count;

    public Task UpsertAsync(PolicyChunk chunk, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default)
    {
        _items[chunk.Id] = (chunk, vector);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PolicySearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector, int top, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PolicySearchResult> results = _items.Values
            .Select(item => new PolicySearchResult(
                item.Chunk,
                // 1.0 = same meaning, ~0 = unrelated. Throws if the vectors have different lengths
                // (e.g. documents and query embedded with different models).
                TensorPrimitives.CosineSimilarity(queryVector.Span, item.Vector.Span)))
            .OrderByDescending(r => r.Score)
            .Take(top)
            .ToList();

        return Task.FromResult(results);
    }
}