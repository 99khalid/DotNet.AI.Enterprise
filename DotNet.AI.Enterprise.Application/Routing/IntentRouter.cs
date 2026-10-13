using System.ComponentModel;
using System.Numerics.Tensors;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotNet.AI.Enterprise.Application.Routing;

[JsonConverter(typeof(JsonStringEnumConverter<Intent>))]
public enum Intent
{
    /// <summary>Company rules: leave policy, attendance, remote work, benefits...</summary>
    Policy,

    /// <summary>The employee's own data: balance, profile, manager, holidays, today's date.</summary>
    Profile,

    /// <summary>The employee wants to submit a leave request.</summary>
    LeaveRequest,

    /// <summary>Not an HR topic.</summary>
    Other
}

public sealed record RouteDecision(Intent Intent, string Method, double Score);

/// <summary>One part of the employee's message and the specialist it goes to.</summary>
public sealed record RoutedPart(string Text, RouteDecision Decision);

/// <summary>
/// Triage: decides which specialist handles a question. It only routes — it never answers.
///
/// Hybrid:
///   1. Embeddings — compare the question with example phrases for each intent (fast, no LLM call).
///   2. If that is not confident (low score, or two intents too close), ask the LLM with structured output.
/// </summary>
public sealed class IntentRouter(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IChatClient chatClient,
    ILogger<IntentRouter> logger)
{
    // Thresholds for bge-m3 — like MinScore they are model-specific: re-check them when the embedding model changes.
    private const double MinScore = 0.60;
    private const double MinMargin = 0.04;

    private static readonly Dictionary<Intent, string[]> Examples = new()
    {
        [Intent.Policy] =
        [
            "إيه سياسة الإجازة المرضية؟", "ينفع أشتغل من البيت؟", "لو اتأخرت بيحصل إيه؟", "الشركة بتدفع تأمين طبي؟",
            "كام يوم إجازة الزواج؟", "بدل المواصلات كام؟", "مواعيد العمل إيه؟", "What is the remote work policy?"
        ],
        [Intent.Profile] =
        [
            "رصيد إجازاتي كام؟", "عندي كام يوم عطلة؟", "مين مديري؟", "أنا شغال في الشركة من إمتى؟",
            "فاضلي كام يوم إجازة؟", "إيه الإجازات الرسمية السنة دي؟", "النهارده كام في الشهر؟", "What is my leave balance?"
        ],
        [Intent.LeaveRequest] =
        [
            "عايز إجازة من 1 لـ 3 نوفمبر", "قدّملي إجازة سنوية", "اطلبلي أجازة يومين الأسبوع الجاي", "محتاج إجازة مرضية بكرة",
            "عايز أقدّم طلب إجازة علشان فرح أخويا", "خدلي إجازة من الأحد للتلات", "I want to take annual leave next week", "Request leave from Dec 1 to Dec 3"
        ],
        [Intent.Other] =
        [
            "اكتبلي قصيدة", "احكيلي نكتة", "الجو عامل إيه النهارده؟", "مين كسب الماتش امبارح؟",
            "اكتبلي كود بايثون", "ترجملي الجملة دي", "What's the capital of France?", "Write me a story"
        ],
    };

    private readonly SemaphoreSlim _init = new(1, 1);
    private List<(Intent Intent, ReadOnlyMemory<float> Vector)>? _exampleVectors;

    // Where one message may hold two requests: "عايز إجازة بكرة وكمان قولي سياسة العمل من البيت".
    private static readonly Regex PartSeparator = new(
        @"[؟?!\n]+|\s+(?:و\s*كمان|وكذلك|وبالمرة|and also|also)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MinPartLength = 8;

    /// <summary>
    /// Multi-intent: splits the message into parts and routes each one, so "leave request + policy question"
    /// becomes two routes instead of losing the second request. Conservative on purpose: it only splits when
    /// every part is routed confidently by embeddings and the parts really have different intents —
    /// otherwise the whole message gets a single route.
    /// </summary>
    public async Task<IReadOnlyList<RoutedPart>> RouteAllAsync(string question, CancellationToken cancellationToken = default)
    {
        var parts = SplitIntoParts(question);

        if (parts.Count > 1)
        {
            var scored = new List<(string Text, Score Score)>();
            foreach (var part in parts)
                scored.Add((part, await ScoreAsync(part, cancellationToken)));

            if (scored.All(p => p.Score.Confident) && scored.Select(p => p.Score.Intent).Distinct().Count() > 1)
            {
                // Neighbouring parts with the same intent go to the specialist together.
                var merged = new List<RoutedPart>();
                foreach (var (text, score) in scored)
                {
                    if (merged.Count > 0 && merged[^1].Decision.Intent == score.Intent)
                        merged[^1] = merged[^1] with { Text = $"{merged[^1].Text}، {text}" };
                    else
                        merged.Add(new RoutedPart(text, new RouteDecision(score.Intent, "embeddings", Math.Round(score.Value, 3))));
                }

                logger.LogInformation("Route \"{Question}\" split into {Count} parts: {Parts}", question, merged.Count,
                    string.Join(" | ", merged.Select(p => $"{p.Decision.Intent}: \"{p.Text}\"")));
                return merged;
            }
        }

        return [new RoutedPart(question, await RouteAsync(question, cancellationToken))];
    }

    public async Task<RouteDecision> RouteAsync(string question, CancellationToken cancellationToken = default)
    {
        var score = await ScoreAsync(question, cancellationToken);

        if (score.Confident)
        {
            logger.LogInformation("Route \"{Question}\" → {Intent} (embeddings) — {Scores}", question, score.Intent, score.Details);
            return new RouteDecision(score.Intent, "embeddings", Math.Round(score.Value, 3));
        }

        // Not confident: let the model decide — one small, constrained task (pick one value of an enum).
        var intent = await ClassifyWithModelAsync(question, cancellationToken);
        logger.LogInformation("Route \"{Question}\" → {Intent} (llm, embeddings unsure) — {Scores}", question, intent, score.Details);
        return new RouteDecision(intent, "llm", Math.Round(score.Value, 3));
    }

    /// <summary>Embeddings only: the nearest intent and whether it is confident (high score, clear margin).</summary>
    private async Task<Score> ScoreAsync(string text, CancellationToken cancellationToken)
    {
        var examples = await GetExampleVectorsAsync(cancellationToken);
        var vector = await embedder.GenerateVectorAsync(text, cancellationToken: cancellationToken);

        // Best similarity per intent (nearest example wins).
        var ranked = examples
            .GroupBy(e => e.Intent)
            .Select(g => (Intent: g.Key, Score: (double)g.Max(e => TensorPrimitives.CosineSimilarity(vector.Span, e.Vector.Span))))
            .OrderByDescending(x => x.Score)
            .ToList();

        var (best, second) = (ranked[0], ranked[1]);
        return new Score(
            best.Intent,
            best.Score,
            Confident: best.Score >= MinScore && best.Score - second.Score >= MinMargin,
            Details: string.Join(" | ", ranked.Select(r => $"{r.Intent} {r.Score:0.000}")));
    }

    /// <summary>Splits on question marks and "and also" words; tiny fragments ("والجمعة") stay with the previous part.</summary>
    private static List<string> SplitIntoParts(string question)
    {
        var parts = new List<string>();
        foreach (var raw in PartSeparator.Split(question))
        {
            var part = raw.Trim(' ', '،', ',', '.');
            if (part.Length == 0) continue;

            if (part.Length < MinPartLength && parts.Count > 0)
                parts[^1] = $"{parts[^1]} {part}";
            else
                parts.Add(part);
        }
        return parts;
    }

    private sealed record Score(Intent Intent, double Value, bool Confident, string Details);

    private async Task<Intent> ClassifyWithModelAsync(string question, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Prompts.RouterSystem),
            new(ChatRole.User, question)
        ];

        var response = await chatClient.GetResponseAsync<IntentChoice>(
            messages, new ChatOptions { Temperature = 0 }, cancellationToken: cancellationToken);

        // If the model's answer is unusable, Policy is the safest default: read-only, grounded in documents.
        return response.TryGetResult(out var choice) ? choice.Intent : Intent.Policy;
    }

    /// <summary>The examples are embedded once, on first use, in a single batched call.</summary>
    private async Task<List<(Intent Intent, ReadOnlyMemory<float> Vector)>> GetExampleVectorsAsync(CancellationToken cancellationToken)
    {
        if (_exampleVectors is not null) return _exampleVectors;

        await _init.WaitAsync(cancellationToken);
        try
        {
            if (_exampleVectors is not null) return _exampleVectors;

            var flat = Examples.SelectMany(e => e.Value.Select(text => (e.Key, text))).ToList();
            var vectors = await embedder.GenerateAsync(flat.Select(f => f.text), cancellationToken: cancellationToken);

            _exampleVectors = flat.Select((f, i) => (f.Key, vectors[i].Vector)).ToList();
            return _exampleVectors;
        }
        finally
        {
            _init.Release();
        }
    }

    [Description("The single best intent for the employee's message.")]
    private sealed record IntentChoice(Intent Intent);
}
