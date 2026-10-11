using System.ComponentModel.DataAnnotations;

namespace DotNet.AI.Enterprise.Application.Options;

/// <summary>
/// Settings for the embedding model (text → vector). Independent from the chat model:
/// you can chat with a local model and embed with Gemini, or the other way around.
/// </summary>
public sealed class EmbeddingOptions : IValidatableObject
{
    public const string SectionName = "Embedding";

    public AiProvider Provider { get; init; } = AiProvider.Gemini;

    [Required]
    public string Model { get; init; } = string.Empty;

    /// <summary>Optional — falls back to AI:ApiKey when empty.</summary>
    public string? ApiKey { get; init; }

    [Url]
    public string? Endpoint { get; init; }

    /// <summary>Optional vector size (e.g. 768). Null = the model's default.</summary>
    public int? Dimensions { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider == AiProvider.OpenAICompatible && string.IsNullOrWhiteSpace(Endpoint))
            yield return new ValidationResult("Endpoint is required for the OpenAICompatible provider.", [nameof(Endpoint)]);
    }
}