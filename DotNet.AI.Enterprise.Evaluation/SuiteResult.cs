namespace DotNet.AI.Enterprise.Evaluation;

/// <summary>
/// One number to track over time. Quality metrics are compared with the baseline (a drop larger than
/// Tolerance is a regression); Info metrics are only shown.
/// </summary>
public sealed record Metric(string Name, double Value, string Display, bool IsQuality = true, double Tolerance = 0.0001, bool IsRate = false);

public sealed record SuiteResult(
    string Suite,
    IReadOnlyList<Metric> Metrics,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string>? Details = null);

public static class Format
{
    public static Metric Rate(string name, int passed, int total) =>
        new(name, total == 0 ? 0 : (double)passed / total, $"{(total == 0 ? 0 : 100.0 * passed / total):0}%  ({passed}/{total})", IsRate: true);
}
