using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Domain;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DotNet.AI.Enterprise.Application;

/// <summary>
/// One chat session: owns the conversation history, the system prompt and the tools.
/// Knows nothing about the console or HTTP — both the ConsoleApp and the WebApi use it.
/// Not thread-safe by design: one request at a time per conversation (enforced by _gate).
/// </summary>
public sealed class HrAssistant(
    IChatClient chatClient,
    HrTools tools,
    IEmployeeRepository employees,
    ICurrentUser currentUser,
    TimeProvider time,
    IOptions<AiOptions> options,
    PolicyRetriever retriever,
    IOptions<RagOptions> ragOptions)
{
    private readonly AiOptions _options = options.Value;
    private readonly List<ChatMessage> _history = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UsageDetails? LastUsage { get; private set; }

    /// <summary>Excerpts handed to the model for the last question (Always mode) — shown in the UI.</summary>
    public IReadOnlyList<PolicySearchResult> LastSources { get; private set; } = [];
    public UsageDetails SessionUsage { get; } = new();

    /// <summary>Streams the answer. History is only updated if the whole call succeeds.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> AskStreamingAsync(
        string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Two requests on the same conversation at once would corrupt the history.
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("This conversation is already processing a message.");

        try
        {
            await foreach (var update in AskCoreAsync(prompt, cancellationToken))
                yield return update;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> AskCoreAsync(
        string prompt, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastSources = [];
        var userMessage = new ChatMessage(ChatRole.User, prompt);
        var alwaysRetrieve = ragOptions.Value.Mode == RetrievalMode.Always;

        var systemPrompt = Prompts.HrAssistantSystem +
                           (alwaysRetrieve ? Prompts.ClassicPolicyRules : Prompts.AgenticPolicyRules);

        // The model has no memory: every request re-sends system prompt + history + new question.
        List<ChatMessage> messages = [new(ChatRole.System, systemPrompt), .. _history];

        if (alwaysRetrieve)
            messages.Add(await BuildPolicyContextAsync(prompt, cancellationToken));

        messages.Add(userMessage);

        var chatOptions = new ChatOptions
        {
            Temperature = 0.3f,     // mostly factual, a little natural phrasing
            // Always mode: the code already searched, so the model doesn't get the search tool.
            Tools = tools.AsAITools(includePolicySearch: !alwaysRetrieve)
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        var response = updates.ToChatResponse();
        _history.Add(userMessage);
        _history.AddRange(response.Messages); // includes tool calls + tool results
        TrimHistory();

        LastUsage = response.Usage;
        if (response.Usage is not null) SessionUsage.Add(response.Usage);
    }

    /// <summary>
    /// Classic RAG: the code searches on EVERY question and hands the excerpts to the model,
    /// so a small model never has to decide "should I search?".
    /// Sent as its own User message right BEFORE the question — not a System message (some chat
    /// templates, e.g. qwen2.5 on Ollama, drop system messages that aren't first), and not merged
    /// into the question (the Arabic excerpts would fool LanguagePinningChatClient on English questions).
    /// Not saved in history: each new question retrieves fresh excerpts.
    /// </summary>
    private async Task<ChatMessage> BuildPolicyContextAsync(string question, CancellationToken cancellationToken)
    {
        var results = await retriever.SearchAsync(question, cancellationToken);
        LastSources = results;

        var excerpts = results.Count == 0
            ? "(No relevant policy excerpts were found.)"
            : string.Join("\n\n", results.Select(r => $"Source: [{r.Chunk.Citation}]\n{r.Chunk.Text}"));

        return new ChatMessage(ChatRole.User, $"Policy excerpts for my next question:\n\n{excerpts}");
    }

    /// <summary>Structured Output: free text in, typed C# object out.</summary>
    public async Task<(LeaveRequestDraft? Draft, IReadOnlyList<string> Errors)> ExtractLeaveRequestAsync(
        string text, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().Date);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, string.Format(Prompts.LeaveExtractionSystem, today.ToString("yyyy-MM-dd (dddd)"))),
            new(ChatRole.User, text)
        ];

        // Temperature 0: extraction must be deterministic, no creativity.
        var response = await chatClient.GetResponseAsync<LeaveRequestDraft>(
            messages, new ChatOptions { Temperature = 0 }, cancellationToken: cancellationToken);

        LastUsage = response.Usage;
        if (response.Usage is not null) SessionUsage.Add(response.Usage);

        if (!response.TryGetResult(out var draft))
            return (null, ["الموديل رجّع رد مش مطابق للشكل المطلوب."]);

        var me = employees.GetById(currentUser.EmployeeId)!;
        return (draft, draft.Validate(me, today));
    }

    public void ClearHistory() => _history.Clear();

    public int HistoryCount => _history.Count;

    // Keeps the context window (and cost) bounded. Always start on a user message so a
    // tool call is never separated from its result.
    private void TrimHistory()
    {
        if (_history.Count <= _options.MaxHistoryMessages) return;

        _history.RemoveRange(0, _history.Count - _options.MaxHistoryMessages);
        while (_history.Count > 0 && _history[0].Role != ChatRole.User)
            _history.RemoveAt(0);
    }
}
