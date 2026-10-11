using System.ComponentModel.DataAnnotations;

namespace DotNet.AI.Enterprise.Application.Options;

public enum AiProvider
{
    /// <summary>Google Gemini via the Google.GenAI SDK.</summary>
    Gemini,

    /// <summary>Any OpenAI-compatible endpoint: OpenAI, Ollama, LM Studio, vLLM, ...</summary>
    OpenAICompatible
}

/// <summary>
/// Strongly-typed settings bound from the "AI" section of configuration.
/// ApiKey is NOT stored in appsettings.json — it comes from User Secrets (dev)
/// or the AI__ApiKey environment variable (production).
/// </summary>
public sealed class AiOptions : IValidatableObject
{
    public const string SectionName = "AI";

    public AiProvider Provider { get; init; } = AiProvider.Gemini;

    [Required]
    public string Model { get; init; } = string.Empty;

    public string? ApiKey { get; init; }

    /// <summary>Required for OpenAICompatible (e.g. http://localhost:11434/v1 for Ollama).</summary>
    [Url]
    public string? Endpoint { get; init; }

    [Range(1, 600)]
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Max messages kept in chat history — bounds context size and cost per request.</summary>
    [Range(2, 200)]
    public int MaxHistoryMessages { get; init; } = 20;

    /// <summary>Optional, from the provider's pricing page. When set, the console shows session cost.</summary>
    public decimal? InputCostPerMillionTokens { get; init; }

    public decimal? OutputCostPerMillionTokens { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider == AiProvider.Gemini && string.IsNullOrWhiteSpace(ApiKey))
            yield return new ValidationResult("ApiKey is required for the Gemini provider.", [nameof(ApiKey)]);

        if (Provider == AiProvider.OpenAICompatible && string.IsNullOrWhiteSpace(Endpoint))
            yield return new ValidationResult("Endpoint is required for the OpenAICompatible provider.", [nameof(Endpoint)]);
    }
}
