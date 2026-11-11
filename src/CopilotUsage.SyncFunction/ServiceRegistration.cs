using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotUsage.SyncFunction;

public static class ServiceRegistration
{
    public static IServiceCollection AddSyncServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SyncApiOptions>().Bind(configuration.GetSection(SyncApiOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDelayer, TaskDelayer>();
        services.AddSingleton<ISyncTokenProvider, ManagedIdentityTokenProvider>();
        // The per-attempt timeout is enforced by SyncRunner with a cancellation token; the client's own limit must not cut in first.
        services.AddHttpClient<ISyncApiClient, HttpSyncApiClient>(http => http.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<SyncRunner>();
        return services;
    }
}
