namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>
/// Stores chunks with their vectors and finds the closest ones to a query vector.
/// Everything is async: a real store is a database, and I/O must not block.
/// </summary>
public interface IPolicyStore
{
    /// <summary>Id → content hash of every stored chunk. Lets the indexer skip chunks that didn't change.</summary>
    Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(PolicyChunk chunk, string contentHash, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default);

    Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PolicySearchResult>> SearchAsync(ReadOnlyMemory<float> queryVector, int top, CancellationToken cancellationToken = default);
}