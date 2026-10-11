namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>Stores chunks with their vectors and finds the closest ones to a query vector.</summary>
public interface IPolicyStore
{
    int Count { get; }

    Task UpsertAsync(PolicyChunk chunk, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PolicySearchResult>> SearchAsync(ReadOnlyMemory<float> queryVector, int top, CancellationToken cancellationToken = default);
}