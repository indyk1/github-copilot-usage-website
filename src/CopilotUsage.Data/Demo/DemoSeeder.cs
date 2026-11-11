using CopilotUsage.Core.Calculations;
using CopilotUsage.Core.Models;

namespace CopilotUsage.Data.Demo;

/// <summary>Writes the generated demo dataset through the same upsert path the real sync uses.</summary>
public sealed class DemoSeeder(UsageWriter writer, TimeProvider time, Microsoft.Extensions.Options.IOptions<CopilotUsage.Core.CopilotOptions> options)
{
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var set = DemoDataGenerator.Generate(now, options.Value.CreditsPerSeat);
        var rows = 0;

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
