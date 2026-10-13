using System.Text.Json;

namespace DotNet.AI.Enterprise.Evaluation;

/// <summary>
/// The numbers of the last accepted run (baseline.json, committed with the code). Every run is compared with it:
/// a quality metric that drops more than its tolerance is a regression, and the process exits with 1.
/// </summary>
public static class Baseline
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Dictionary<string, double> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path)) ?? []
            : [];

    public static void Save(string path, IEnumerable<SuiteResult> results) =>
        File.WriteAllText(path, JsonSerializer.Serialize(
            results.SelectMany(r => r.Metrics.Select(m => (Key: Key(r, m), m.Value)))
                   .ToDictionary(x => x.Key, x => Math.Round(x.Value, 4)), Json));

    /// <summary>Prints the summary table; returns true if any quality metric regressed.</summary>
    public static bool PrintSummary(IEnumerable<SuiteResult> results, IReadOnlyDictionary<string, double> baseline)
    {
        var regressed = false;

        Console.WriteLine($"\n{"Suite",-11}{"Metric",-20}{"Value",-42}{"Baseline",-10}");
        Console.WriteLine(new string('─', 90));

        foreach (var result in results)
        foreach (var metric in result.Metrics)
        {
            var change = "";
            if (baseline.TryGetValue(Key(result, metric), out var before))
            {
                var delta = metric.Value - before;
                var isRegression = metric.IsQuality && delta < -metric.Tolerance;
                regressed |= isRegression;

                change = $"{Show(metric, before),-10}" + (Math.Abs(delta) < 0.0001 ? "="
                    : isRegression ? $"▼ {Show(metric, delta)}  REGRESSION"
                    : delta > 0 ? $"▲ +{Show(metric, delta)}" : $"▼ {Show(metric, delta)}");
            }

            Console.WriteLine($"{result.Suite,-11}{metric.Name,-20}{metric.Display,-42}{change}");
        }

        return regressed;

        // Rates are stored 0–1 and shown as %, other values (MRR, groundedness, seconds) as they are.
        static string Show(Metric m, double v) => m.IsRate ? $"{v * 100:0.#}%" : $"{v:0.00#}";
    }

    private static string Key(SuiteResult r, Metric m) => $"{r.Suite}/{m.Name}";
}
