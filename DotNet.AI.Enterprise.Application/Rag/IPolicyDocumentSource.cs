namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>Where the policy documents come from (files today; SharePoint / a DB tomorrow).</summary>
public interface IPolicyDocumentSource
{
    Task<IReadOnlyList<PolicyDocument>> LoadAsync(CancellationToken cancellationToken = default);
}
