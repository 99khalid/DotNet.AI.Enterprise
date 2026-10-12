using System.Text;

namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>
/// Splits a Markdown document into one chunk per "## " heading.
/// Splitting on the document's own structure keeps each chunk a complete, meaningful unit.
/// </summary>
public static class MarkdownChunker
{
    public static IEnumerable<PolicyChunk> Chunk(PolicyDocument document)
    {
        string? section = null;
        var body = new StringBuilder();

        foreach (var rawLine in document.Content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("# "))
                continue; // document title — already in document.Title

            if (line.StartsWith("## "))
            {
                if (section is not null && body.Length > 0)
                    yield return new PolicyChunk(ChunkId(document.Title, section), document.Title, section, body.ToString().Trim());

                section = line[3..].Trim();
                body.Clear();
                continue;
            }

            if (section is not null)
                body.AppendLine(line);
        }

        if (section is not null && body.Length > 0)
            yield return new PolicyChunk(ChunkId(document.Title, section), document.Title, section, body.ToString().Trim());
    }
    // Stable id = document + article title. A position-based id ("#3") would change for every
    // article after an inserted one, and incremental indexing would re-embed all of them.
    private static string ChunkId(string documentTitle, string section) => $"{documentTitle} › {section}";
}