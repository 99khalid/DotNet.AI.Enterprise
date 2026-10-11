using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.WebApi.Conversations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DotNet.AI.Enterprise.WebApi.Endpoints;

public static class HrEndpoints
{
    private const int MaxInputLength = 4000;

    public static IEndpointRouteBuilder MapHrEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", GetInfo);
        api.MapPost("/conversations/{conversationId:guid}/messages", SendMessage);
        api.MapDelete("/conversations/{conversationId:guid}", DeleteConversation);
        api.MapPost("/leave-requests/extract", ExtractLeave);

        return app;
    }

    private static InfoResponse GetInfo(IOptions<AiOptions> options, IEmployeeRepository employees, ICurrentUser currentUser) =>
        new(options.Value.Provider.ToString(), options.Value.Model, employees.GetById(currentUser.EmployeeId)?.FullName ?? "?");

    /// <summary>
    /// Streams the answer as Server-Sent Events: "tool" → "text" (many) → "usage", or "error".
    /// The browser shows each piece as soon as it arrives.
    /// </summary>
    private static IResult SendMessage(
        Guid conversationId, SendMessageRequest request, ConversationStore store, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > MaxInputLength)
            return TypedResults.BadRequest(new ErrorEvent($"Message must be 1–{MaxInputLength} characters."));

        var assistant = store.GetOrCreate(conversationId);
        var logger = loggerFactory.CreateLogger(nameof(HrEndpoints));

        return TypedResults.ServerSentEvents(StreamAnswer(assistant, request.Message, logger, cancellationToken));
    }

    private static async IAsyncEnumerable<SseItem<object>> StreamAnswer(
        HrAssistant assistant, string message, ILogger logger, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Once streaming starts the HTTP status (200) is already sent, so failures must be
        // reported as an "error" event instead of a 500. C# can't `yield` inside try/catch,
        // hence the manual enumerator.
        await using var updates = assistant.AskStreamingAsync(message, cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate update;
            string? error = null;

            try
            {
                if (!await updates.MoveNextAsync()) break;
                update = updates.Current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Chat request failed");
                error = ex is OperationCanceledException ? "The AI provider timed out. Try again." : ex.Message;
                update = null!;
            }

            if (error is not null)
            {
                yield return new SseItem<object>(new ErrorEvent(error), "error");
                yield break;
            }

            foreach (var call in update.Contents.OfType<FunctionCallContent>())
                yield return new SseItem<object>(new ToolCallEvent(call.Name, call.Arguments), "tool");

            if (!string.IsNullOrEmpty(update.Text))
                yield return new SseItem<object>(new TextEvent(update.Text), "text");
        }

        if (assistant.LastSources.Count > 0)
            yield return new SseItem<object>(
                new SourcesEvent(assistant.LastSources.Select(s => new SourceDto(s.Chunk.Citation, Math.Round(s.Score, 3))).ToList()),
                "sources");

        yield return new SseItem<object>(
            UsageDto.From(assistant.LastUsage, assistant.SessionUsage, assistant.HistoryCount), "usage");
    }

    private static NoContent DeleteConversation(Guid conversationId, ConversationStore store)
    {
        store.Remove(conversationId);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ExtractLeave(
        ExtractLeaveRequest request, HrAssistant assistant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxInputLength)
            return TypedResults.BadRequest(new ErrorEvent($"Text must be 1–{MaxInputLength} characters."));

        var (draft, errors) = await assistant.ExtractLeaveRequestAsync(request.Text, cancellationToken);

        return TypedResults.Ok(new ExtractLeaveResponse(
            draft, errors, UsageDto.From(assistant.LastUsage, assistant.SessionUsage, 0)));
    }
}
