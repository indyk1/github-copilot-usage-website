using CopilotUsage.Data;
using CopilotUsage.Data.Entities;
using CopilotUsage.Tests.Support;
using CopilotUsage.Tests.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotUsage.Tests.Sync;

/// <summary>Demo mode must never delete real data, and real mode must never show demo users. Judged from the stored sync log.</summary>
public class DemoGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static Task AddRealDataAsync(TestHost host, SyncStatus status = SyncStatus.Succeeded) => host.WithDbAsync(async db =>
    {
        db.SyncRuns.Add(new SyncRun { StartedAt = Now, FinishedAt = Now, Status = status, Trigger = SyncTrigger.Scheduled, IsDemo = false });
        db.Seats.Add(new Seat { Login = "real-person", FirstSeenAt = Now, LastSyncedAt = Now });
        db.UserModelMonthly.Add(new UserModelMonthly { MonthStart = new DateOnly(2026, 11, 1), UserLogin = "real-person", Model = "m", Credits = 42m });
        await db.SaveChangesAsync();
        return 0;
    });

    private static Task<string> Snapshot(TestHost host) => host.SnapshotAsync();

    [Fact]
    public async Task Demo_on_with_real_data_refuses_and_leaves_the_data_untouched()
    {
        await using var host = new TestHost(Now, demo: true);
        await AddRealDataAsync(host);
        var before = await Snapshot(host);

        var result = await host.Sync.RunAsync(SyncTrigger.Manual); // the Refresh button takes the same path as startup

        Assert.Equal(SyncStatus.Failed, result.Status);
        Assert.Contains("ConnectionStrings__Default", Assert.Single(result.Steps).Error);
        Assert.Contains("Demo__Enabled=false", result.Steps[0].Error);
        Assert.Equal(before, await Snapshot(host));
        Assert.Equal(DataOrigin.Real, await host.WithDbAsync(db => DatabaseOrigin.GetAsync(db)));
        Assert.Equal(42m, await host.WithDbAsync(db => db.UserModelMonthly.SumAsync(x => x.Credits)));
        Assert.Equal(["real-person"], await host.WithDbAsync(db => db.Seats.Select(s => s.Login).ToListAsync()));
        // The refusal is recorded as a failed run, so the home page shows why; it does not make the database look demo.
        Assert.Equal("Failed", (await host.QueryAsync(q => q.GetLastSyncAsync()))!.Status);
    }

    [Fact]
    public async Task Data_with_no_sync_log_is_treated_as_real_and_protected()
    {
        await using var host = new TestHost(Now, demo: true);
        await host.WithDbAsync(async db => { db.Seats.Add(new Seat { Login = "x", FirstSeenAt = Now, LastSyncedAt = Now }); await db.SaveChangesAsync(); return 0; });
        Assert.Equal(SyncStatus.Failed, (await host.Sync.RunAsync(SyncTrigger.Demo)).Status);
        Assert.Equal(1, await host.WithDbAsync(db => db.Seats.CountAsync()));
    }

    [Fact]
    public async Task Demo_on_with_only_demo_data_reseeds_as_before()
    {
        await using var host = new TestHost(Now, demo: true);
        Assert.Equal(SyncStatus.Succeeded, (await host.Sync.RunAsync(SyncTrigger.Demo)).Status);
        Assert.Equal(DataOrigin.DemoOnly, await host.WithDbAsync(db => DatabaseOrigin.GetAsync(db)));
        Assert.Equal(SyncStatus.Succeeded, (await host.Sync.RunAsync(SyncTrigger.Manual)).Status);
        Assert.Equal(25, await host.WithDbAsync(db => db.Seats.CountAsync()));
    }

    [Fact]
    public async Task Demo_on_with_an_empty_database_seeds()
    {
        await using var host = new TestHost(Now, demo: true);
        Assert.Equal(DataOrigin.Empty, await host.WithDbAsync(db => DatabaseOrigin.GetAsync(db)));
        Assert.Equal(SyncStatus.Succeeded, (await host.Sync.RunAsync(SyncTrigger.Demo)).Status);
        Assert.Equal(25, await host.WithDbAsync(db => db.Seats.CountAsync()));
    }

    [Fact]
    public async Task A_failed_real_run_alone_does_not_make_the_database_real()
    {
        await using var host = new TestHost(Now, demo: true);
        await host.WithDbAsync(async db =>
        {
            db.SyncRuns.Add(new SyncRun { StartedAt = Now, Status = SyncStatus.Failed, Trigger = SyncTrigger.Scheduled, IsDemo = false });
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(SyncStatus.Succeeded, (await host.Sync.RunAsync(SyncTrigger.Demo)).Status);
    }

    [Fact]
    public async Task Demo_off_with_demo_data_is_recognised_and_clearing_removes_all_of_it()
    {
        await using var seeded = new TestHost(Now, demo: true);
        await seeded.Sync.RunAsync(SyncTrigger.Demo);
        await seeded.WithDbAsync(async db =>
        {
            Assert.Equal(DataOrigin.DemoOnly, await DatabaseOrigin.GetAsync(db));
            await DatabaseOrigin.ClearDemoAsync(db);
            Assert.Equal(0, await db.Seats.CountAsync());
            Assert.Equal(0, await db.SeatSnapshots.CountAsync());
            Assert.Equal(0, await db.CreditUsageDaily.CountAsync());
            Assert.Equal(0, await db.UserModelMonthly.CountAsync());
            Assert.Equal(0, await db.UserMetricDaily.CountAsync());
            Assert.Equal(0, await db.UserMetricBreakdown.CountAsync());
            Assert.Equal(0, await db.SyncRuns.CountAsync());
            Assert.Equal(DataOrigin.Empty, await DatabaseOrigin.GetAsync(db));
            return 0;
        });
    }

    [Fact]
    public async Task Demo_off_startup_clears_demo_data_and_keeps_real_data()
    {
        var dir = Path.Combine(Path.GetTempPath(), "copilot-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var conn = $"Data Source={Path.Combine(dir, "g.db")}";
        try
        {
            // First start in demo mode fills the file; second start with demo off must clear it (and still serve).
            using (var demo = new AppFactory("Production", new() { ["ConnectionStrings:Default"] = conn })) { _ = demo.Client(); }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var live = new AppFactory("Production", new()
            {
                ["ConnectionStrings:Default"] = conn, ["Demo:Enabled"] = "false", ["GitHub:Org"] = "acme", ["GitHub:Token"] = "t",
                ["GitHub:BaseUrl"] = "http://127.0.0.1:9/", ["Sync:RunOnStartup"] = "false",
            });
            _ = live.Client();
            using var scope = live.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CopilotUsageDbContext>();
            Assert.Equal(0, await db.Seats.CountAsync());
            Assert.Equal(DataOrigin.Empty, await DatabaseOrigin.GetAsync(db));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
