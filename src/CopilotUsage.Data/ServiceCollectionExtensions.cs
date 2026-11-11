using CopilotUsage.Data.Demo;
using CopilotUsage.Data.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotUsage.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCopilotUsageData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<CopilotUsageDbContext>(o => o.UseSqlite(connectionString));
        services.AddScoped<UsageWriter>();
        services.AddScoped<UsageQueries>();
        services.AddScoped<DemoSeeder>();
        return services;
    }
}
