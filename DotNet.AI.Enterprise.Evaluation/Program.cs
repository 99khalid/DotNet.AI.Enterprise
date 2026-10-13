using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Leave;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Rag;
using DotNet.AI.Enterprise.Application.Routing;
using DotNet.AI.Enterprise.Evaluation;
using DotNet.AI.Enterprise.Evaluation.Suites;
using DotNet.AI.Enterprise.Infrastructure;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// Offline evaluation of the whole system on the golden dataset (Data/golden.json).
//   dotnet run --project DotNet.AI.Enterprise.Evaluation                   free: router, retrieval, extraction, tools, fact recall
//   dotnet run --project DotNet.AI.Enterprise.Evaluation -- --judge        + groundedness graded by the judge model (cached)
//   dotnet run --project DotNet.AI.Enterprise.Evaluation -- --save-baseline   accept this run as the new baseline
//   dotnet run --project DotNet.AI.Enterprise.Evaluation -- --only=Router,Retrieval   quick check of some suites
Console.OutputEncoding = System.Text.Encoding.UTF8;
var useJudge = args.Contains("--judge");
var saveBaseline = args.Contains("--save-baseline");
var only = args.FirstOrDefault(a => a.StartsWith("--only="))?["--only=".Length..].Split(',') ?? [];
if (saveBaseline && only.Length > 0) throw new ArgumentException("--save-baseline needs a full run (no --only).");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args.Where(a => !a.StartsWith("--")).ToArray(),
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddUserSecrets<Program>(optional: true); // the judge's API key

// The real system, exactly as the front ends build it.
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

using var host = builder.Build();
var services = host.Services;
await services.GetRequiredService<PolicyIndexer>().IndexAsync();

var ai = services.GetRequiredService<IOptions<AiOptions>>().Value;
var embedding = services.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
var rag = services.GetRequiredService<IOptions<RagOptions>>().Value;
var cases = GoldenCase.Load(Path.Combine(AppContext.BaseDirectory, "Data", "golden.json"));
var projectDir = FindProjectDirectory();

// Reporting: every scored answer is stored under .eval/ so `dotnet aieval report` can render an HTML report.
// With the judge, its responses are cached there too: an unchanged answer is never sent to the judge twice.
var storage = Path.Combine(projectDir, ".eval");
var executionName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var factsOnly = DiskBasedReportingConfiguration.Create(storage, [new FactRecallEvaluator()], executionName: executionName);
var judged = useJudge ? CreateJudgedReporting(builder.Configuration) : null;

Console.WriteLine($"""
    ══ HR Assistant evaluation ══ {DateTime.Now:yyyy-MM-dd HH:mm}
    System : chat {ai.Model} (temperature {ai.Temperature}) · embeddings {embedding.Model} · RAG {rag.Mode}, top {rag.TopK}, min score {rag.MinScore} · {rag.Store}
    Judge  : {(judged is null ? "off (run with --judge)" : builder.Configuration["Judge:Model"])}
    Cases  : {cases.Count}

    """);

var suites = new (string Name, Func<Task<SuiteResult>> Run)[]
{
    ("Router", () => RouterSuite.RunAsync(cases, services.GetRequiredService<IntentRouter>(), default)),
    ("Retrieval", () => RetrievalSuite.RunAsync(cases, services.GetRequiredService<PolicyRetriever>(), rag.TopK, default)),
    ("Extraction", () => ExtractionSuite.RunAsync(cases, services.GetRequiredService<LeaveRequestExtractor>(), default)),
    ("Agents", () => AgentSuite.RunAsync(cases, services, factsOnly, judged, default)),
};

var results = new List<SuiteResult>();
foreach (var (name, suite) in suites.Where(s => only.Length == 0 || only.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
    results.Add(await Run(name, suite));

foreach (var result in results)
{
    if (result.Details is { Count: > 0 })
    {
        Console.WriteLine($"\n── {result.Suite}: confusion matrix");
        result.Details.ToList().ForEach(line => Console.WriteLine("   " + line));
    }
    if (result.Failures.Count > 0)
    {
        Console.WriteLine($"\n── {result.Suite}: {result.Failures.Count} failed");
        result.Failures.ToList().ForEach(f => Console.WriteLine("   ✗ " + f));
    }
}

var baselinePath = Path.Combine(projectDir, "baseline.json");
var regressed = Baseline.PrintSummary(results, Baseline.Load(baselinePath));

if (saveBaseline)
{
    Baseline.Save(baselinePath, results);
    Console.WriteLine($"\nBaseline saved: {baselinePath}");
}
else if (regressed)
{
    Console.WriteLine("\n❌ Regression against the baseline.");
}

Console.WriteLine($"\nHTML report: dotnet aieval report -p \"{storage}\" -o report.html");
return regressed && !saveBaseline ? 1 : 0;

static async Task<SuiteResult> Run(string name, Func<Task<SuiteResult>> suite)
{
    Progress.Start(name);
    var result = await suite();
    Progress.End();
    return result;
}

ReportingConfiguration? CreateJudgedReporting(IConfiguration configuration)
{
    var section = configuration.GetSection("Judge");
    var judge = new AiOptions
    {
        Provider = section.GetValue<AiProvider>("Provider"),
        Model = section["Model"] ?? "",
        ApiKey = section["ApiKey"] ?? configuration["AI:ApiKey"],
        Endpoint = section["Endpoint"],
        TimeoutSeconds = section.GetValue("TimeoutSeconds", 120)
    };

    if (judge.Provider == AiProvider.Gemini && string.IsNullOrWhiteSpace(judge.ApiKey))
    {
        Console.WriteLine("⚠️  No API key for the judge (Judge:ApiKey or AI:ApiKey) — running without it.");
        return null;
    }

    return DiskBasedReportingConfiguration.Create(
        storage,
        [new GroundednessEvaluator(), new FactRecallEvaluator()],
        new ChatConfiguration(DotNet.AI.Enterprise.Infrastructure.DependencyInjection.CreateChatClient(judge)),
        enableResponseCaching: true,
        executionName: executionName);
}

static string FindProjectDirectory()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (dir.GetFiles("DotNet.AI.Enterprise.Evaluation.csproj").Length > 0)
            return dir.FullName;
    return Directory.GetCurrentDirectory();
}
