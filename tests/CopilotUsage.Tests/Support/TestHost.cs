using CopilotUsage.Core;
using CopilotUsage.Data;
using CopilotUsage.Data.Demo;
using CopilotUsage.Data.Queries;
using CopilotUsage.GitHub;
using CopilotUsage.Web.Config;
using CopilotUsage.Web.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CopilotUsage.Tests.Support;

/// <summary>An in-memory SQLite database (real migrations applied) plus the app services, with a fake clock and GitHub client.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    public ServiceProvider Services { get; }
    public FakeTimeProvider Time { get; }
    public FakeGitHubClient GitHub { get; }

    public TestHost(DateTimeOffset now, bool demo = false, FakeGitHubClient? github = null)
    {
        Time = new FakeTimeProvider(now);
        GitHub = github ?? new FakeGitHubClient();

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var s = new ServiceCollection();
        s.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        s.AddSingleton<TimeProvider>(Time);
        s.AddSingleton(Options.Create(new CopilotOptions()));
        s.AddSingleton(Options.Create(new DemoOptions { Enabled = demo }));
        s.AddSingleton(Options.Create(new SyncOptions()));
        s.AddDbContext<CopilotUsageDbContext>(o => o.UseSqlite(_connection));
        s.AddScoped<UsageWriter>();
        s.AddScoped<UsageQueries>();
        s.AddScoped<DemoSeeder>();
        s.AddSingleton<IGitHubClient>(GitHub);
        s.AddSingleton<ISyncService, SyncService>();
        Services = s.BuildServiceProvider();

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CopilotUsageDbContext>().Database.Migrate();
    }

    public ISyncService Sync => Services.GetRequiredService<ISyncService>();

    public async Task<T> WithDbAsync<T>(Func<CopilotUsageDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<CopilotUsageDbContext>());
    }

    public async Task<T> QueryAsync<T>(Func<UsageQueries, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UsageQueries>());
    }

    /// <summary>A comparable text dump of every data table (everything except SyncRun), ordered by natural keys.</summary>
    public Task<string> SnapshotAsync() => WithDbAsync(async db =>
    {
        var parts = new List<string>
        {
            "seats:" + string.Join("|", (await db.Seats.AsNoTracking().ToListAsync()).OrderBy(x => x.Login).Select(x => $"{x.Login},{x.IsActive},{x.PlanType},{x.AssigningTeam}")),
            "snap:" + string.Join("|", (await db.SeatSnapshots.AsNoTracking().ToListAsync()).Select(x => $"{x.Day},{x.TotalSeats}")),
            "org:" + string.Join("|", (await db.CreditUsageDaily.AsNoTracking().ToListAsync()).OrderBy(x => x.Day).ThenBy(x => x.Model).Select(x => $"{x.Day},{x.UserLogin},{x.Model},{x.Credits},{x.Amount}")),
            "umm:" + string.Join("|", (await db.UserModelMonthly.AsNoTracking().ToListAsync()).OrderBy(x => x.MonthStart).ThenBy(x => x.UserLogin).ThenBy(x => x.Model).Select(x => $"{x.MonthStart},{x.UserLogin},{x.Model},{x.Credits}")),
            "umd:" + string.Join("|", (await db.UserMetricDaily.AsNoTracking().ToListAsync()).OrderBy(x => x.Day).ThenBy(x => x.UserLogin).Select(x => $"{x.Day},{x.UserLogin},{x.AiCredits},{x.Prompts},{x.SuggestionsGenerated},{x.UsedAgent}")),
            "umb:" + string.Join("|", (await db.UserMetricBreakdown.AsNoTracking().ToListAsync()).OrderBy(x => x.Day).ThenBy(x => x.UserLogin).ThenBy(x => x.Dimension).ThenBy(x => x.Key).Select(x => $"{x.Day},{x.UserLogin},{x.Dimension},{x.Key},{x.Interactions},{x.Requests},{x.Version}")),
        };
        return string.Join("\n", parts);
    });

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
