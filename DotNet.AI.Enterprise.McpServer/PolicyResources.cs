using System.ComponentModel;
using System.Text;
using DotNet.AI.Enterprise.Application.Rag;
using ModelContextProtocol.Server;

namespace DotNet.AI.Enterprise.McpServer;

/// <summary>
/// Resources = data the USER (or the host app) chooses to attach, like attaching a file to a chat.
/// Tools are chosen by the model: SearchPolicies returns the 3 relevant excerpts, a resource is the whole document.
/// </summary>
[McpServerResourceType]
public sealed class PolicyResources(IPolicyDocumentSource documents)
{
    [McpServerResource(UriTemplate = "hr://policies", Name = "policies", MimeType = "text/markdown")]
    [Description("The list of HR policy documents, with the URI of each one.")]
    public async Task<string> ListPolicies(CancellationToken cancellationToken)
    {
        var all = await documents.LoadAsync(cancellationToken);

        var index = new StringBuilder("# HR policy documents\n\n");
        for (var i = 0; i < all.Count; i++)
            index.AppendLine($"- {all[i].Title} → hr://policies/{i + 1}");

        return index.ToString();
    }

    [McpServerResource(UriTemplate = "hr://policies/{number}", Name = "policy", MimeType = "text/markdown")]
    [Description("One HR policy document, in full (number from the hr://policies list).")]
    public async Task<string> GetPolicy(int number, CancellationToken cancellationToken)
    {
        var all = await documents.LoadAsync(cancellationToken);

        return number >= 1 && number <= all.Count
            ? all[number - 1].Content
            : throw new ArgumentOutOfRangeException(nameof(number), $"There are {all.Count} policy documents (1–{all.Count}).");
    }
}
