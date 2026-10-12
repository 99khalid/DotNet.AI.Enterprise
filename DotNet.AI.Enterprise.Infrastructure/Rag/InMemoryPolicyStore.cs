using System.Collections.Concurrent;
using System.Numerics.Tensors;
using DotNet.AI.Enterprise.Application.Rag;

namespace DotNet.AI.Enterprise.Infrastructure.Rag;

/// <summary>
/// A vector store in ~30 lines: keep every vector in memory and compare the query against ALL of them.
/// Fine for hundreds of chunks. Lost on restart, so everything is re-embedded on every start.
/// </summary>
public sealed class InMemoryPolicyStore : IPolicyStore
{
    private readonly ConcurrentDictionary<string, (PolicyChunk Chunk, string Hash, ReadOnlyMemory<float> Vector)> _items = new();

    public Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(_items.ToDictionary(i => i.Key, i => i.Value.Hash));

    public Task UpsertAsync(PolicyChunk chunk, string contentHash, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default)
    {
        _items[chunk.Id] = (chunk, contentHash, vector);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        foreach (var id in ids)
            _items.TryRemove(id, out _);
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