namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>One searchable piece of a policy document — one "## article".</summary>
public sealed record PolicyChunk(string Id, string Document, string Section, string Text)
{
    /// <summary>Shown to the user so every answer can be traced back to its source.</summary>
    public string Citation => $"{Document} › {Section}";

    /// <summary>
    /// What we actually embed: the text PLUS where it came from (contextual header).
    /// "21 يوم" alone is ambiguous; "[سياسة الإجازات › الإجازة السنوية] 21 يوم" is not.
    /// </summary>
    public string EmbeddingText => $"[{Citation}]\n{Text}";
}

public sealed record PolicySearchResult(PolicyChunk Chunk, float Score);

public sealed record PolicyDocument(string Title, string Content);