using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Leave;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Routing;
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

        return app;
    }

    private static InfoResponse GetInfo(IOptions<AiOptions> options, IEmployeeRepository employees, ICurrentUser currentUser) =>
        new(options.Value.Provider.ToString(), options.Value.Model, employees.GetById(currentUser.EmployeeId)?.FullName ?? "?");

    /// <summary>
    /// Server-Sent Events, per request in the message: "route" (which specialist got it), then either
    /// the agent's "tool" / "text" / "sources" / "usage", or the leave workflow's "step" / "proposal" / "result".
    /// A message with two requests ("leave + policy question") produces two routes, handled in order.
    /// </summary>
    private static IResult SendMessage(
        Guid conversationId, SendMessageRequest request, ConversationStore store, LeaveWorkflowService workflows,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > MaxInputLength)
            return TypedResults.BadRequest(new ErrorEvent($"Message must be 1–{MaxInputLength} characters."));

        var assistant = store.GetOrCreate(conversationId);
        var logger = loggerFactory.CreateLogger(nameof(HrEndpoints));

        return TypedResults.ServerSentEvents(StreamChat(assistant, workflows, request.Message, logger, cancellationToken));
    }

    private static async IAsyncEnumerable<SseItem<object>> StreamChat(
        HrAssistant assistant, LeaveWorkflowService workflows, string message, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 1. Route — the router only decides who answers (one route per request in the message).
        var failed = new StrongBox<bool>();
        IReadOnlyList<RoutedPart>? parts = null;
        await foreach (var sse in SseStreaming.MapAsync(RouteOnce(assistant, message, cancellationToken), p =>
                       {
                           parts = p;
                           return [];
                       }, logger, failed, cancellationToken))
            yield return sse;

        if (failed.Value || parts is null) yield break;

        foreach (var part in parts)
        {
            var route = part.Decision;
            yield return new SseItem<object>(
                new RouteEvent(route.Intent.ToString(), route.Method, route.Score, parts.Count > 1 ? part.Text : null), "route");

            // 2a. Leave requests always go to the workflow — the model never decides the steps.
            if (route.Intent == Intent.LeaveRequest)
            {
                await foreach (var sse in SseStreaming.MapAsync(workflows.StartAsync(part.Text, cancellationToken), LeaveWorkflowEndpoints.ToEvents, logger, failed, cancellationToken))
                    yield return sse;
            }
            // 2b. Everything else goes to the chosen specialist agent.
            else
            {
                await foreach (var sse in SseStreaming.MapAsync(assistant.AskStreamingAsync(part.Text, route.Intent, cancellationToken), MapAgent, logger, failed, cancellationToken))
                    yield return sse;

                if (failed.Value) yield break;

                if (assistant.LastSources.Count > 0)
                    yield return new SseItem<object>(
                        new SourcesEvent(assistant.LastSources.Select(s => new SourceDto(s.Chunk.Citation, Math.Round(s.Score, 3))).ToList()),
                        "sources");

                yield return new SseItem<object>(
                    UsageDto.From(assistant.LastUsage, assistant.SessionUsage, assistant.HistoryCount), "usage");
            }

            if (failed.Value) yield break;
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<RoutedPart>> RouteOnce(
        HrAssistant assistant, string message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await assistant.RouteAllAsync(message, cancellationToken);
    }
    private static IEnumerable<SseItem<object>> MapAgent(ChatResponseUpdate update)
    {
        foreach (var call in update.Contents.OfType<FunctionCallContent>())
            yield return new SseItem<object>(new ToolCallEvent(call.Name, call.Arguments), "tool");

        if (!string.IsNullOrEmpty(update.Text))
            yield return new SseItem<object>(new TextEvent(update.Text), "text");
    }

    private static NoContent DeleteConversation(Guid conversationId, ConversationStore store)
    {
        store.Remove(conversationId);
        return TypedResults.NoContent();
    }
}
