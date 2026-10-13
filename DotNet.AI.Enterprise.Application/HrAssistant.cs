using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Domain;
using DotNet.AI.Enterprise.Application.Leave;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Rag;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    private readonly LeaveRequestExtractor _extractor;
    private readonly LeaveRequestService _leave;

    private readonly AIAgent _agent;
    private readonly InMemoryChatHistoryProvider _history;
    private readonly PolicyContextProvider? _policyContext;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ToolApprovalRequestContent> _pendingApprovals = [];
    private readonly ILogger<HrAssistant> _logger;
    private AgentSession? _session;

    public HrAssistant(
        IChatClient chatClient,
        HrTools tools,
        LeaveRequestExtractor extractor,
        LeaveRequestService leave,
        IOptions<AiOptions> options,
        PolicyRetriever retriever,
        IOptions<RagOptions> ragOptions,
        ILogger<HrAssistant> logger)
    {
        _extractor = extractor;
        _leave = leave;
        _logger = logger;

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
    public IAsyncEnumerable<ChatResponseUpdate> AskStreamingAsync(string prompt, CancellationToken cancellationToken = default) =>
        RunStreamingAsync(new ChatMessage(ChatRole.User, prompt), cancellationToken);

    public bool HasPendingApproval(string requestId) => _pendingApprovals.ContainsKey(requestId);

    /// <summary>
    /// Human-in-the-loop: the employee's decision on a tool call the agent paused for.
    /// Comes from a button in the UI — never from chat text the model could be tricked by.
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> RespondToApprovalAsync(
        string requestId, bool approved, CancellationToken cancellationToken = default)
    {
        // An approval is bound to ONE specific call and can be used only once.
        if (!_pendingApprovals.Remove(requestId, out var request))
            throw new InvalidOperationException("This approval request doesn't exist or was already answered.");

        _logger.LogInformation("Approval {RequestId} for {Tool}: {Decision}",
            requestId, (request.ToolCall as FunctionCallContent)?.Name, approved ? "APPROVED" : "REJECTED");

        var response = request.CreateResponse(approved, approved ? null : "The employee rejected this request.");
        return RunStreamingAsync(new ChatMessage(ChatRole.User, [response]), cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> RunStreamingAsync(
        ChatMessage message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Two requests on the same conversation at once would corrupt the history.
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("This conversation is already processing a message.");

        try
        {
            // The session holds this conversation's state (history). One per conversation.
            _session ??= await _agent.CreateSessionAsync(cancellationToken);

            var updates = new List<AgentResponseUpdate>();
            await foreach (var update in _agent.RunStreamingAsync(message, _session, cancellationToken: cancellationToken))
            {
                updates.Add(update);

                // The agent paused before a tool that needs approval: remember it until the employee decides.
                foreach (var request in update.Contents.OfType<ToolApprovalRequestContent>())
                {
                    _pendingApprovals[request.RequestId] = request;
                    var call = request.ToolCall as FunctionCallContent;
                    _logger.LogInformation("Approval {RequestId} requested for {Tool} with {Arguments}",
                        request.RequestId, call?.Name, call?.Arguments is null ? "" : string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}")));
                }

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
        var (draft, usage) = await _extractor.ExtractAsync(text, cancellationToken);

        LastUsage = usage;
        if (usage is not null) SessionUsage.Add(usage);

        return (draft, _leave.Validate(draft));
    }

    /// <summary>A new session = a fresh conversation with no history.</summary>
    public void ClearHistory()
    {
        _session = null;
        _pendingApprovals.Clear();
    }
}