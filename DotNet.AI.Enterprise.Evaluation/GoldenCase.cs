using System.Text.Json;
using System.Text.Json.Serialization;
using DotNet.AI.Enterprise.Application.Routing;
using DotNet.AI.Enterprise.Domain;

namespace DotNet.AI.Enterprise.Evaluation;

/// <summary>
/// One question with its known correct behaviour. Each stage is scored only on the fields a case sets:
/// Intents → router, Source / Unanswerable → retrieval, Tool → tool selection, Facts → answer, Leave → extraction.
/// </summary>
public sealed record GoldenCase(
    string Id,
    string Question,
    Intent[] Intents,
    string? Source = null,
    bool Unanswerable = false,
    string[]? Facts = null,
    string? Tool = null,
    ExpectedLeave? Leave = null)
{
    /// <summary>The single intent, or null for a multi-intent case.</summary>
    public Intent? SingleIntent => Intents.Length == 1 ? Intents[0] : null;

    public static IReadOnlyList<GoldenCase> Load(string path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        return JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(path), options)
               ?? throw new InvalidOperationException($"No cases in {path}.");
    }
}

/// <summary>What the extractor should return. Null means "not in the text — must stay null".</summary>
public sealed record ExpectedLeave(LeaveType? Type, DateOnly? StartDate, DateOnly? EndDate);
