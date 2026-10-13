using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

namespace DotNet.AI.Enterprise.WebApi.Endpoints;

internal static class SseStreaming
{
    /// <summary>
    /// Maps a stream to Server-Sent Events. Once streaming starts the HTTP status (200) is already sent,
    /// so a failure must become an "error" event instead of a 500. C# can't `yield` inside try/catch,
    /// hence the manual enumerator. Returns false (via <paramref name="failed"/>) if the stream failed.
    /// </summary>
    public static async IAsyncEnumerable<SseItem<object>> MapAsync<T>(
        IAsyncEnumerable<T> source,
        Func<T, IEnumerable<SseItem<object>>> map,
        ILogger logger,
        StrongBox<bool> failed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var items = source.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            T item;
            string? error = null;

            try
            {
                if (!await items.MoveNextAsync()) break;
                item = items.Current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Streaming request failed");
                error = ex is OperationCanceledException ? "The AI provider timed out. Try again." : ex.Message;
                item = default!;
            }

            if (error is not null)
            {
                failed.Value = true;
                yield return new SseItem<object>(new ErrorEvent(error), "error");
                yield break;
            }

            foreach (var sse in map(item))
                yield return sse;
        }
    }
}
