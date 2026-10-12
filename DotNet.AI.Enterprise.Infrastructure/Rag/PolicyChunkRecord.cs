using DotNet.AI.Enterprise.Application.Rag;

namespace DotNet.AI.Enterprise.Infrastructure.Rag;

/// <summary>
/// The database row shape. Kept separate from PolicyChunk (the application's shape) so storage
/// details — hash, vector, column names — never leak into the Application layer.
/// </summary>
public sealed class PolicyChunkRecord
{
    public string Id { get; set; } = string.Empty;
    public string Document { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public ReadOnlyMemory<float> Embedding { get; set; }

    public static PolicyChunkRecord From(PolicyChunk chunk, string contentHash, ReadOnlyMemory<float> embedding) => new()
    {
        Id = chunk.Id,
        Document = chunk.Document,
        Section = chunk.Section,
        Text = chunk.Text,
        ContentHash = contentHash,
        Embedding = embedding
    };

    public PolicyChunk ToChunk() => new(Id, Document, Section, Text);
}