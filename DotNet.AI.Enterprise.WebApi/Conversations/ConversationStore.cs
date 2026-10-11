using DotNet.AI.Enterprise.Application;
using Microsoft.Extensions.Caching.Memory;

namespace DotNet.AI.Enterprise.WebApi.Conversations;

/// <summary>
/// Keeps one HrAssistant (and therefore one chat history) per conversation id, in server memory.
/// Idle conversations expire automatically. In-memory means: lost on restart and not shared
/// between servers — the Production stage replaces this with Redis or a database.
/// </summary>
public sealed class ConversationStore(IMemoryCache cache, IServiceProvider services)
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public HrAssistant GetOrCreate(Guid conversationId) =>
        cache.GetOrCreate(Key(conversationId), entry =>
        {
            entry.SlidingExpiration = IdleTimeout;
            return services.GetRequiredService<HrAssistant>();
        })!;

    public void Remove(Guid conversationId) => cache.Remove(Key(conversationId));

    private static string Key(Guid id) => $"conversation:{id}";
}
