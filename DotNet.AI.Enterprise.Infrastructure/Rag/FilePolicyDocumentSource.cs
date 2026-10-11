using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Rag;
using Microsoft.Extensions.Options;

namespace DotNet.AI.Enterprise.Infrastructure.Rag;

/// <summary>Reads every .md file in the configured folder. The first "# " line is the document title.</summary>
public sealed class FilePolicyDocumentSource(IOptions<RagOptions> options) : IPolicyDocumentSource
{
    public async Task<IReadOnlyList<PolicyDocument>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, options.Value.DocumentsPath);
        var documents = new List<PolicyDocument>();

        foreach (var file in Directory.EnumerateFiles(folder, "*.md").Order())
        {
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var title = content.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim()
                        ?? Path.GetFileNameWithoutExtension(file);

            documents.Add(new PolicyDocument(title, content));
        }

        return documents;
    }
}