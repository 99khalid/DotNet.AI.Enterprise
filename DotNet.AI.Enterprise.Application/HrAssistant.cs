using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Domain;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Rag;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DotNet.AI.Enterprise.Application;

/// <summary>
/// One conversation with the HR agent. The agent itself (instructions, tools, RAG, history) is built
/// with Microsoft Agent Framework; this class keeps the same public API, so the ConsoleApp and the
/// WebApi didn't change.
/// Not thread-safe by design: one request at a time per conversation (enforced by _gate).
/// </summary>
public sealed class HrAssistant
{
    private readonly IChatClient _chatClient;
    private readonly IEmployeeRepository _employees;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _time;

    private readonly AIAgent _agent;
    private readonly InMemoryChatHistoryProvider _history;
    private readonly PolicyContextProvider? _policyContext;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentSession? _session;

    public HrAssistant(
        IChatClient chatClient,
        HrTools tools,
        IEmployeeRepository employees,
        ICurrentUser currentUser,
        TimeProvider time,
        IOptions<AiOptions> options,
        PolicyRetriever retriever,
        IOptions<RagOptions> ragOptions)
    {
        _chatClient = chatClient;
        _employees = employees;
        _currentUser = currentUser;
        _time = time;

        var alwaysRetrieve = ragOptions.Value.Mode == RetrievalMode.Always;

        // Short-term memory: the framework stores the conversation per session.
        // The reducer keeps only the last N messages (replaces our old TrimHistory).
#pragma warning disable MEAI001 // MessageCountingChatReducer is still marked experimental
        _history = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new MessageCountingChatReducer(options.Value.MaxHistoryMessages)
        });
#pragma warning restore MEAI001

        // Always mode: classic RAG runs before every question (replaces our old BuildPolicyContextAsync).
        _policyContext = alwaysRetrieve ? new PolicyContextProvider(retriever) : null;

        _agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "HrAssistant",
            Description = "Answers employees' HR questions: leave balance, profile, holidays and company policies.",
            ChatOptions = new ChatOptions
            {
                Instructions = Prompts.HrAssistantSystem + (alwaysRetrieve ? Prompts.ClassicPolicyRules : Prompts.AgenticPolicyRules),
                Temperature = 0.3f,
                // Always mode: the code already searched, so the agent doesn't get the search tool.
                Tools = tools.AsAITools(includePolicySearch: !alwaysRetrieve)
            },
            ChatHistoryProvider = _history,
            AIContextProviders = _policyContext is null ? [] : [_policyContext]
        });
    }

    public UsageDetails? LastUsage { get; private set; }

    public UsageDetails SessionUsage { get; } = new();

    /// <summary>Excerpts handed to the agent for the last question (Always mode) — shown in the UI.</summary>
    public IReadOnlyList<PolicySearchResult> LastSources => _policyContext?.LastSources ?? [];

    public int HistoryCount => _session is null ? 0 : _history.GetMessages(_session).Count;

    /// <summary>Streams the answer as it is generated.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> AskStreamingAsync(
        string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Two requests on the same conversation at once would corrupt the history.
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("This conversation is already processing a message.");

        try
        {
            // The session holds this conversation's state (history). One per conversation.
            _session ??= await _agent.CreateSessionAsync(cancellationToken);

            var updates = new List<AgentResponseUpdate>();
            await foreach (var update in _agent.RunStreamingAsync(prompt, _session, cancellationToken: cancellationToken))
            {
                updates.Add(update);
                yield return update.AsChatResponseUpdate();
            }

            var response = updates.ToAgentResponse();
            LastUsage = response.Usage;
            if (response.Usage is not null) SessionUsage.Add(response.Usage);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Structured Output: free text in, typed C# object out. Not an agent task — a single, deterministic call.</summary>
    public async Task<(LeaveRequestDraft? Draft, IReadOnlyList<string> Errors)> ExtractLeaveRequestAsync(
        string text, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().Date);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, string.Format(Prompts.LeaveExtractionSystem, today.ToString("yyyy-MM-dd (dddd)"))),
            new(ChatRole.User, text)
        ];

        // Temperature 0: extraction must be deterministic, no creativity.
        var response = await _chatClient.GetResponseAsync<LeaveRequestDraft>(
            messages, new ChatOptions { Temperature = 0 }, cancellationToken: cancellationToken);

        LastUsage = response.Usage;
        if (response.Usage is not null) SessionUsage.Add(response.Usage);

        if (!response.TryGetResult(out var draft))
            return (null, ["الموديل رجّع رد مش مطابق للشكل المطلوب."]);

        var me = _employees.GetById(_currentUser.EmployeeId)!;
        return (draft, draft.Validate(me, today));
    }

    /// <summary>A new session = a fresh conversation with no history.</summary>
    public void ClearHistory() => _session = null;
}