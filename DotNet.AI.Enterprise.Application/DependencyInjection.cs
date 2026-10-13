using DotNet.AI.Enterprise.Application.Leave;
using DotNet.AI.Enterprise.Application.Rag;
using Microsoft.Extensions.DependencyInjection;

namespace DotNet.AI.Enterprise.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddTransient<HrTools>();
        services.AddSingleton<PolicyIndexer>();
        services.AddSingleton<PolicyRetriever>();
        services.AddSingleton<LeaveRequestExtractor>();
        services.AddSingleton<LeaveRequestService>();
        services.AddSingleton<LeaveWorkflowService>(); // singleton: holds the runs paused for approval
        services.AddTransient<HrAssistant>(); // transient: every conversation gets its own history
        return services;
    }
}
