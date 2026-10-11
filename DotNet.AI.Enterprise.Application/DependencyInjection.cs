using Microsoft.Extensions.DependencyInjection;

namespace DotNet.AI.Enterprise.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddTransient<HrTools>();
        services.AddTransient<HrAssistant>(); // transient: every conversation gets its own history
        return services;
    }
}
