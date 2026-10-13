using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Domain;
using DotNet.AI.Enterprise.Application.Leave;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Rag;
using DotNet.AI.Enterprise.Application.Routing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DotNet.AI.Enterprise.Application;

/// <summary>
/// One conversation with the HR assistant, built as a team of specialists:
///
///   question → IntentRouter ─┬─ Profile      → Profile agent (4 read-only tools: balance, profile, holidays, date)
///                            ├─ Policy       → Policy agent  (RAG, no tools that change data)
///                            ├─ LeaveRequest → leave workflow (handled by the caller, see LeaveWorkflowService)
///                            └─ Other        → fixed reply, no model call
///
/// Each agent has its own short instructions, its own tools and its own history (session).
/// Not thread-safe by design: one request at a time per conversation (enforced by _gate).
/// </summary>
public sealed class HrAssistant
{
    private readonly IntentRouter _router;
    private readonly PrivacyGuard _privacy;
    private readonly LeaveRequestExtractor _extractor;
    private readonly LeaveRequestService _leave;
    private readonly Specialist _profile;
    private readonly Specialist _policy;
    private readonly PolicyContextProvider? _policyContext;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HrAssistant(
        IChatClient chatClient,
        HrTools tools,
        IntentRouter router,
        PrivacyGuard privacy,
        LeaveRequestExtractor extractor,
        LeaveRequestService leave,
        IOptions<AiOptions> options,
        PolicyRetriever retriever,
        IOptions<RagOptions> ragOptions)
    {
        _router = router;
        _privacy = privacy;
        _extractor = extractor;
        _leave = leave;

        var maxHistory = options.Value.MaxHistoryMessages;
        var alwaysRetrieve = ragOptions.Value.Mode == RetrievalMode.Always;

        _profile = new Specialist(chatClient, maxHistory, new ChatClientAgentOptions
        {
            Name = "ProfileAgent",
            Description = "The employee's own data: leave balance, profile, manager, public holidays, today's date.",
            ChatOptions = new ChatOptions
            {
                Instructions = Prompts.ProfileAgentSystem,
                Temperature = options.Value.Temperature,
                Tools = tools.ProfileTools()
            }
        });

        // Always mode: classic RAG runs before every question; Agentic mode: the agent gets the search tool.
        _policyContext = alwaysRetrieve ? new PolicyContextProvider(retriever) : null;

        _policy = new Specialist(chatClient, maxHistory, new ChatClientAgentOptions
        {
            Name = "PolicyAgent",
            Description = "Company rules: leave policy, attendance, remote work, benefits.",
            ChatOptions = new ChatOptions
            {
                Instructions = Prompts.PolicyAgentSystem + (alwaysRetrieve ? Prompts.ClassicPolicyRules : Prompts.AgenticPolicyRules),
                Temperature = options.Value.Temperature,
                Tools = tools.PolicyTools(includePolicySearch: !alwaysRetrieve)
            },
            AIContextProviders = _policyContext is null ? [] : [_policyContext]
        });
    }

    public UsageDetails? LastUsage { get; private set; }

    public UsageDetails SessionUsage { get; } = new();

    /// <summary>Policy excerpts used for the last answer (Always mode) — shown in the UI.</summary>
    public IReadOnlyList<PolicySearchResult> LastSources { get; private set; } = [];

    public int HistoryCount => _profile.HistoryCount + _policy.HistoryCount;

    /// <summary>Step 1: decide which specialist should handle the question.</summary>
    public Task<RouteDecision> RouteAsync(string prompt, CancellationToken cancellationToken = default) =>
        _router.RouteAsync(prompt, cancellationToken);

    /// <summary>Step 1 (multi-intent): one route per request in the message — the caller runs them in order.</summary>
    public Task<IReadOnlyList<RoutedPart>> RouteAllAsync(string prompt, CancellationToken cancellationToken = default) =>
        _router.RouteAllAsync(prompt, cancellationToken);

    /// <summary>Step 2: the chosen specialist streams its answer. LeaveRequest goes to the workflow instead.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> AskStreamingAsync(
        string prompt, Intent intent, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (intent == Intent.LeaveRequest)
            throw new InvalidOperationException("Leave requests are handled by the leave workflow, not by an agent.");

        LastSources = [];

        if (intent == Intent.Other)
        {
            // Off-topic: answered by code — no model call, so no chance of a poem.
            LastUsage = null;
            yield return new ChatResponseUpdate(ChatRole.Assistant, Prompts.OffTopicReply);
            yield break;
        }

        if (intent == Intent.Profile && _privacy.FindOtherEmployee(prompt) is { } other)
        {
            // Another employee's data: answered by code, the model never sees this question.
            LastUsage = null;
            yield return new ChatResponseUpdate(ChatRole.Assistant, PrivacyGuard.Reply(other));
            yield break;
        }

        // Two requests on the same conversation at once would corrupt the history.
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("This conversation is already processing a message.");

        try
        {
            var specialist = intent == Intent.Policy ? _policy : _profile;

            var updates = new List<AgentResponseUpdate>();
            await foreach (var update in specialist.RunStreamingAsync(prompt, cancellationToken))
            {
                updates.Add(update);
                yield return update.AsChatResponseUpdate();
            }

            var response = updates.ToAgentResponse();
            LastUsage = response.Usage;
            if (response.Usage is not null) SessionUsage.Add(response.Usage);
            if (intent == Intent.Policy) LastSources = _policyContext?.LastSources ?? [];
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
        var (draft, usage) = await _extractor.ExtractAsync(text, cancellationToken);

        LastUsage = usage;
        if (usage is not null) SessionUsage.Add(usage);

        return (draft, _leave.Validate(draft));
    }

    /// <summary>A new session for every agent = a fresh conversation with no history.</summary>
    public void ClearHistory()
    {
        _profile.Reset();
        _policy.Reset();
    }

    /// <summary>One specialist agent with its own history (a separate session per agent).</summary>
    private sealed class Specialist
    {
        private readonly AIAgent _agent;
        private readonly InMemoryChatHistoryProvider _history;
        private AgentSession? _session;

        public Specialist(IChatClient chatClient, int maxHistoryMessages, ChatClientAgentOptions options)
        {
            // Short-term memory; the reducer keeps only the last N messages.
#pragma warning disable MEAI001 // MessageCountingChatReducer is still marked experimental
            _history = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
            {
                ChatReducer = new MessageCountingChatReducer(maxHistoryMessages)
            });
#pragma warning restore MEAI001

            options.ChatHistoryProvider = _history;
            _agent = chatClient.AsAIAgent(options);
        }

        public int HistoryCount => _session is null ? 0 : _history.GetMessages(_session).Count;

        public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
            string prompt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _session ??= await _agent.CreateSessionAsync(cancellationToken);

            await foreach (var update in _agent.RunStreamingAsync(prompt, _session, cancellationToken: cancellationToken))
                yield return update;
        }

        public void Reset() => _session = null;
    }
}
