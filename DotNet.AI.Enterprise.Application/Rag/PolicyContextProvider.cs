using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotNet.AI.Enterprise.Application.Rag;

/// <summary>
/// Classic RAG as an Agent Framework context provider: before every run, the code searches the
/// policies with the employee's question and hands the excerpts to the agent.
/// </summary>
public sealed class PolicyContextProvider(PolicyRetriever retriever) : AIContextProvider
{
    /// <summary>Excerpts found for the last question — shown in the UI.</summary>
    public IReadOnlyList<PolicySearchResult> LastSources { get; private set; } = [];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var question = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(question))
        {
            LastSources = [];
            return new AIContext();
        }

        LastSources = await retriever.SearchAsync(question, cancellationToken);

        var excerpts = LastSources.Count == 0
            ? "(No relevant policy excerpts were found.)"
            : string.Join("\n\n", LastSources.Select(r => $"Source: [{r.Chunk.Citation}]\n{r.Chunk.Text}"));

        // Added as INSTRUCTIONS, not as a message: the question stays the last message
        // (LanguagePinningChatClient relies on that) and the excerpts are not saved into the history.
        return new AIContext { Instructions = $"Policy excerpts for the employee's current question:\n\n{excerpts}" };
    }
}