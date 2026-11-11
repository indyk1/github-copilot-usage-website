using CopilotUsage.Core.Calculations;
using CopilotUsage.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CopilotUsage.Data.Demo;

/// <summary>
/// Writes the generated demo dataset through the same upsert path the real sync uses. Demo databases hold only
/// synthetic data, so the usage tables are cleared first: switching scenario (or a refresh) always yields exactly that scenario.
/// </summary>
public sealed class DemoSeeder(
    CopilotUsageDbContext db,
    UsageWriter writer,
    TimeProvider time,
    Microsoft.Extensions.Options.IOptions<CopilotUsage.Core.CopilotOptions> options)
{
    public async Task<int> SeedAsync(DemoScenario scenario = DemoScenario.Normal, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var set = DemoDataGenerator.Generate(now, options.Value.CreditsPerSeat, scenario);
        var rows = 0;

        await db.UserMetricBreakdown.ExecuteDeleteAsync(ct);
        await db.UserMetricDaily.ExecuteDeleteAsync(ct);
        await db.UserModelMonthly.ExecuteDeleteAsync(ct);
        await db.CreditUsageDaily.ExecuteDeleteAsync(ct);

        await writer.UpsertSeatSummaryAsync(set.Summary, now, ct);
        rows += await writer.UpsertSeatsAsync(set.Seats, now, ct);

        foreach (var month in set.Months)
        {
            var days = Enumerable.Range(0, month.DaysInMonth).Select(i => month.Start.AddDays(i)).ToList();
            rows += await writer.UpsertOrgDailyCreditsAsync(set.OrgDaily.Where(r => r.Day is { } d && month.Contains(d)), days, ct);
        }

        foreach (var g in set.UserMonthly.GroupBy(l => (l.Month, l.Login)))
        {
            var records = g.Select(l => new CreditUsageRecord(null, l.Login, l.Model, l.Credits, l.Credits * PoolCalculator.DollarsPerCredit));
            rows += await writer.UpsertUserModelMonthAsync(g.Key.Month, g.Key.Login, records, ct);
        }

        rows += await writer.UpsertUserMetricsAsync(set.Metrics, ct);
        return rows;
    }
}
