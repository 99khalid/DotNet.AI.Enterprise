using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace DotNet.AI.Enterprise.Application;

/// <summary>
/// Middleware that keeps the reply in Arabic when the user wrote in Arabic.
///
/// Why middleware: after a tool call, FunctionInvokingChatClient calls the model AGAIN with the
/// tool results appended — English JSON is then the last thing the model reads, and small models
/// answer in English. Only something sitting INSIDE the function-invocation loop can add a hint
/// after those results, on every round trip.
///
/// Registered inside UseFunctionInvocation():  FunctionInvoking → LanguagePinning → provider.
/// </summary>
public sealed partial class LanguagePinningChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private const string Hint = "(Reply in Arabic.)";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(Pin(messages, options), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(Pin(messages, options), options, cancellationToken);

    private static IEnumerable<ChatMessage> Pin(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        // Only conversational requests (the ones with tools). Structured-output extraction is left
        // untouched so the hint can't leak into JSON values.
        if (options?.Tools is not { Count: > 0 })
            return messages;

        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User);

        if (lastUser is null || !ArabicText().IsMatch(lastUser.Text))
            return list;

        // Copy — never mutate the caller's list (it is the conversation history).
        var pinned = new List<ChatMessage>(list);

        if (ReferenceEquals(pinned[^1], lastUser))
            pinned[^1] = new ChatMessage(ChatRole.User, $"{lastUser.Text}\n\n{Hint}");  // first round: next to the question
        else
            pinned.Add(new ChatMessage(ChatRole.User, Hint));                            // after tool results

        return pinned;
    }

    [GeneratedRegex(@"\p{IsArabic}")]
    private static partial Regex ArabicText();
}
