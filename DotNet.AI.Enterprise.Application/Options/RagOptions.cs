using System.ComponentModel.DataAnnotations;

namespace DotNet.AI.Enterprise.Application.Options;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Folder with the policy .md files, relative to the app folder.</summary>
    [Required]
    public string DocumentsPath { get; init; } = "Data/Policies";

    /// <summary>How many chunks to return per search. More = more context, more tokens.</summary>
    [Range(1, 20)]
    public int TopK { get; init; } = 3;

    /// <summary>
    /// Agentic = the model decides when to call SearchPolicies (flexible, needs a capable model).
    /// Always  = the code searches on every question and hands the results to the model (reliable with small models).
    /// </summary>
    public RetrievalMode Mode { get; init; } = RetrievalMode.Agentic;

    /// <summary>
    /// Results below this cosine similarity are dropped, so the code itself can say "not found".
    /// Model-specific: calibrated for bge-m3 — re-check it when you change the embedding model.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinScore { get; init; } = 0.55;
}

public enum RetrievalMode
{
    Agentic,
    Always
}