using DotNet.AI.Enterprise.Domain;
using Microsoft.Extensions.AI;

namespace DotNet.AI.Enterprise.Application.Leave;

/// <summary>
/// Structured Output: free text → LeaveRequestDraft. The only step of leave handling that needs
/// language understanding, so it is the only one that calls the model.
/// </summary>
public sealed class LeaveRequestExtractor(IChatClient chatClient, TimeProvider time)
{
    public async Task<(LeaveRequestDraft Draft, UsageDetails? Usage)> ExtractAsync(string text, CancellationToken cancellationToken = default)
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

        // A malformed answer becomes an empty draft — validation then reports what is missing.
        var draft = response.TryGetResult(out var result) ? result : new LeaveRequestDraft(null, null, null, null);
        return (draft, response.Usage);
    }
}
