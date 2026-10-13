using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Options;
using DotNet.AI.Enterprise.Application.Rag;
using DotNet.AI.Enterprise.Infrastructure.Rag;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel;

namespace DotNet.AI.Enterprise.Infrastructure;

/// <summary>
/// The ONLY place that knows about concrete AI SDKs. Everything else depends on IChatClient,
/// so switching Gemini → OpenAI → a local Ollama model is a configuration change.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Registers every concrete implementation: AI client, data access, identity, clock.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAiChatClient(configuration);
        services.AddPolicyRag(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEmployeeRepository, InMemoryEmployeeRepository>();
        services.AddSingleton<ICurrentUser, DemoCurrentUser>();
        services.AddSingleton<ILeaveRequestRepository, InMemoryLeaveRequestRepository>();

        return services;
    }
    private static IServiceCollection AddPolicyRag(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RagOptions>()
            .Bind(configuration.GetSection(RagOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Same idea as AddChatClient: an abstraction (IEmbeddingGenerator) + a middleware pipeline.
        services
            .AddEmbeddingGenerator(sp => CreateEmbeddingGenerator(
                sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value,
                sp.GetRequiredService<IOptions<AiOptions>>().Value))
            .UseLogging();

        // The only place that changes between stores — Application never knows which one is used.
        var rag = configuration.GetSection(RagOptions.SectionName).Get<RagOptions>() ?? new RagOptions();
        if (rag.Store == PolicyStoreKind.Postgres)
        {
            var connectionString = configuration.GetConnectionString("Policies")
                ?? throw new InvalidOperationException("ConnectionStrings:Policies is required when Rag:Store is Postgres.");

            services.AddPostgresVectorStore(connectionString);
            services.AddSingleton<IPolicyStore, PgVectorPolicyStore>();
        }
        else
        {
            services.AddSingleton<IPolicyStore, InMemoryPolicyStore>();
        }
        services.AddSingleton<IPolicyDocumentSource, FilePolicyDocumentSource>();

        return services;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(EmbeddingOptions options, AiOptions ai)
    {
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? ai.ApiKey : options.ApiKey;

        return options.Provider switch
        {
            AiProvider.Gemini => new Google.GenAI.Client(apiKey: apiKey)
                .AsIEmbeddingGenerator(options.Model, options.Dimensions),

            AiProvider.OpenAICompatible => new OpenAIClient(
                    new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "unused" : apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint!) })
                .GetEmbeddingClient(options.Model)
                .AsIEmbeddingGenerator(options.Dimensions),

            _ => throw new NotSupportedException($"Embedding provider '{options.Provider}' is not supported.")
        };
    }
    private static IServiceCollection AddAiChatClient(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddChatClient(sp => CreateChatClient(sp.GetRequiredService<IOptions<AiOptions>>().Value))
            // Middleware pipeline, same for every provider:
            .UseFunctionInvocation() // runs the tools the model asks for, sends results back, loops until a final answer
            .Use(inner => new LanguagePinningChatClient(inner)) // inside the loop: runs on every round trip, incl. after tool results
            .UseLogging();

        return services;
    }

    /// <summary>
    /// A plain client for one provider, no middleware. Also used on its own, e.g. as the judge model in the evaluation.
    /// </summary>
    public static IChatClient CreateChatClient(AiOptions options) => options.Provider switch
    {
        AiProvider.Gemini => new Google.GenAI.Client(
                apiKey: options.ApiKey,
                httpOptions: new HttpOptions
                {
                    Timeout = options.TimeoutSeconds * 1000,
                    // Retries transient errors (429 / 5xx such as "model overloaded") with exponential backoff.
                    RetryOptions = new HttpRetryOptions { Attempts = 3, MaxDelay = 10 }
                })
            .AsIChatClient(options.Model),

        AiProvider.OpenAICompatible => new OpenAIClient(
                // Local servers such as Ollama ignore the key but the SDK requires a non-empty value.
                new ApiKeyCredential(string.IsNullOrWhiteSpace(options.ApiKey) ? "unused" : options.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(options.Endpoint!),
                    NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
                })
            .GetChatClient(options.Model)
            .AsIChatClient(),

        _ => throw new NotSupportedException($"AI provider '{options.Provider}' is not supported.")
    };
}
