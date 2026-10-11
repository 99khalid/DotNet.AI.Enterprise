using System.ClientModel;
using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Options;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;

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

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEmployeeRepository, InMemoryEmployeeRepository>();
        services.AddSingleton<ICurrentUser, DemoCurrentUser>();

        return services;
    }

    private static IServiceCollection AddAiChatClient(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddChatClient(sp => CreateInnerClient(sp.GetRequiredService<IOptions<AiOptions>>().Value))
            // Middleware pipeline, same for every provider:
            .UseFunctionInvocation() // runs the tools the model asks for, sends results back, loops until a final answer
            .Use(inner => new LanguagePinningChatClient(inner)) // inside the loop: runs on every round trip, incl. after tool results
            .UseLogging();

        return services;
    }

    private static IChatClient CreateInnerClient(AiOptions options) => options.Provider switch
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
