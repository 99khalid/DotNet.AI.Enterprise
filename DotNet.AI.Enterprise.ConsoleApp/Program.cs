using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Rag;
using DotNet.AI.Enterprise.ConsoleApp.Presentation;
using DotNet.AI.Enterprise.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotNet.AI.Enterprise.ConsoleApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Loads appsettings.json, appsettings.{Environment}.json, User Secrets (Development),
        // environment variables and command-line args — in that order of precedence.
        // ContentRootPath = the app's own folder, so appsettings.json is found no matter
        // which directory the app is started from (default is the current working directory).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Same registrations as the WebApi — only the presentation layer differs.
        builder.Services
            .AddApplication()
            .AddInfrastructure(builder.Configuration);

        builder.Services.AddTransient<ChatConsole>();

        using var host = builder.Build();
        await host.StartAsync();
        await host.Services.GetRequiredService<PolicyIndexer>().IndexAsync();

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        await host.Services.GetRequiredService<ChatConsole>().RunAsync(lifetime.ApplicationStopping);

        await host.StopAsync();
    }
}
