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
        var index = 0;

        foreach (var rawLine in document.Content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("# "))
                continue; // document title — already in document.Title

            if (line.StartsWith("## "))
            {
                if (section is not null && body.Length > 0)
                    yield return new PolicyChunk($"{document.Title}#{index++}", document.Title, section, body.ToString().Trim());

                section = line[3..].Trim();
                body.Clear();
                continue;
            }

            if (section is not null)
                body.AppendLine(line);
        }

        if (section is not null && body.Length > 0)
            yield return new PolicyChunk($"{document.Title}#{index}", document.Title, section, body.ToString().Trim());
    }
}