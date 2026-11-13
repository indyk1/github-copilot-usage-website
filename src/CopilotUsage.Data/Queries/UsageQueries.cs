using CopilotUsage.Core;
using CopilotUsage.Core.Calculations;
using CopilotUsage.Core.Models;
using CopilotUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Data.Queries;

/// <summary>
/// Read side for the pages. Pages read only from SQLite. Rows are filtered in SQL and summed in memory
/// (SQLite cannot sum decimals; the volumes are tiny). All methods default to the current UTC billing month.
/// </summary>
public sealed class UsageQueries(CopilotUsageDbContext db, TimeProvider time, IOptions<CopilotOptions> options)
{
    private decimal CreditsPerSeat => options.Value.CreditsPerSeat;

    public BillingMonth CurrentMonth => BillingMonth.Current(time);

    public async Task<HomeSummary> GetHomeSummaryAsync(BillingMonth? month = null, CancellationToken ct = default)
    {
        var m = month ?? CurrentMonth;
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var (seats, activeThisCycle) = await GetSeatCountsAsync(ct);

        var orgRows = await db.CreditUsageDaily.AsNoTracking()
            .Where(x => x.UserLogin == CreditUsageDaily.OrgWide && x.Day >= m.Start && x.Day < m.NextStart)
            .ToListAsync(ct);
        var perDay = orgRows.GroupBy(r => r.Day).ToDictionary(g => g.Key, g => g.Sum(r => r.Credits));
        var used = orgRows.Sum(r => r.Credits);

        var pool = PoolCalculator.Summarize(seats, CreditsPerSeat, used);
        var burn = BurnSeries.Build(m, perDay, pool.Limit, today);

        var split = orgRows.GroupBy(r => r.Model)
            .Select(g => new ModelShare(g.Key, g.Sum(r => r.Credits), g.Sum(r => r.Amount), PoolCalculator.PercentOf(g.Sum(r => r.Credits), used)))
            .OrderByDescending(x => x.Credits).ThenBy(x => x.Model, StringComparer.Ordinal)
            .ToList();

        var lastSync = await GetLatestSyncAsync(ct);
        var asOf = (await GetLatestSyncAsync(ct, successfulOnly: true))?.FinishedAt;

        return new HomeSummary(m, pool, activeThisCycle, ResetCalculator.DaysUntilReset(now), burn, split, asOf, lastSync);
    }

    public async Task<IReadOnlyList<LeaderboardRow>> GetLeaderboardAsync(BillingMonth? month = null, CancellationToken ct = default)
    {
        var m = month ?? CurrentMonth;
        var (seats, _) = await GetSeatCountsAsync(ct);
        var limit = PoolCalculator.Limit(seats, CreditsPerSeat);

        var credits = await GetUserCreditsAsync(m, ct);
        var activeSeats = await db.Seats.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct);
        var avatars = (await db.Seats.AsNoTracking().ToListAsync(ct))
            .ToDictionary(s => s.Login, s => s.AvatarUrl, StringComparer.OrdinalIgnoreCase);

        var users = activeSeats.Select(s => s.Login)
            .Union(credits.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(login => new UserCredits(login, credits.GetValueOrDefault(login)));

        return Leaderboard.Rank(users, CreditsPerSeat, limit)
            .Select(e => new LeaderboardRow(
                e.Rank, e.Login, avatars.GetValueOrDefault(e.Login), e.Credits,
                PoolCalculator.ToDollars(e.Credits), e.ShareOfPoolPercent, e.FairSharePercent, e.OverFairShare))
            .ToList();
    }

    /// <summary>Every seat (including deactivated ones that have usage this month), with the monthly columns of the users table.</summary>
    public async Task<IReadOnlyList<UserRow>> GetUsersTableAsync(BillingMonth? month = null, CancellationToken ct = default)
    {
        var m = month ?? CurrentMonth;
        var credits = await GetUserCreditsAsync(m, ct);
        var seats = await db.Seats.AsNoTracking().ToListAsync(ct);
        var ranks = Leaderboard.Rank(
                seats.Where(s => s.IsActive).Select(s => s.Login).Union(credits.Keys, StringComparer.OrdinalIgnoreCase)
                    .Select(l => new UserCredits(l, credits.GetValueOrDefault(l))),
                CreditsPerSeat, 0m)
            .ToDictionary(e => e.Login, e => e.Rank, StringComparer.OrdinalIgnoreCase);

        var metrics = (await db.UserMetricDaily.AsNoTracking()
                .Where(x => x.Day >= m.Start && x.Day < m.NextStart).ToListAsync(ct))
            .GroupBy(x => x.UserLogin, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Prompts: g.Sum(x => x.Prompts), Gen: g.Sum(x => x.SuggestionsGenerated), Acc: g.Sum(x => x.SuggestionsAccepted), Last: g.Max(x => x.Day)), StringComparer.OrdinalIgnoreCase);

        var topModels = (await db.UserModelMonthly.AsNoTracking().Where(x => x.MonthStart == m.Start).ToListAsync(ct))
            .GroupBy(x => x.UserLogin, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Credits).ThenBy(x => x.Model, StringComparer.Ordinal).First().Model, StringComparer.OrdinalIgnoreCase);

        var rows = new List<UserRow>();
        foreach (var s in seats.Where(s => s.IsActive || credits.ContainsKey(s.Login)))
        {
            var c = credits.GetValueOrDefault(s.Login);
            metrics.TryGetValue(s.Login, out var mt);
            DateTimeOffset? lastActive = s.LastActivityAt;
            if (mt.Last != default)
            {
                var d = new DateTimeOffset(mt.Last.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                if (lastActive is null || d > lastActive) lastActive = d;
            }
            rows.Add(new UserRow(
                s.Login, s.AvatarUrl, ranks.GetValueOrDefault(s.Login, 0), c,
                Leaderboard.FairSharePercent(c, CreditsPerSeat),
                mt.Prompts,
                mt.Gen > 0 ? (decimal)mt.Acc / mt.Gen : null,
                topModels.GetValueOrDefault(s.Login),
                lastActive,
                s.IsActive,
                s.Organization));
        }
        return rows.OrderByDescending(r => r.Credits).ThenBy(r => r.Login, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Null when the login is unknown (no seat row and no usage rows).</summary>
    public async Task<UserDetail?> GetUserDetailAsync(string login, BillingMonth? month = null, CancellationToken ct = default)
    {
        var m = month ?? CurrentMonth;
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        var seat = await db.Seats.AsNoTracking().FirstOrDefaultAsync(s => s.Login == login, ct);
        var modelRows = await db.UserModelMonthly.AsNoTracking()
            .Where(x => x.UserLogin == login && x.MonthStart == m.Start).ToListAsync(ct);
        var daily = await db.UserMetricDaily.AsNoTracking()
            .Where(x => x.UserLogin == login && x.Day >= m.Start && x.Day < m.NextStart).ToListAsync(ct);
        var breakdowns = await db.UserMetricBreakdown.AsNoTracking()
            .Where(x => x.UserLogin == login && x.Day >= m.Start && x.Day < m.NextStart).ToListAsync(ct);

        if (seat is null && modelRows.Count == 0 && daily.Count == 0) return null;
        var canonical = seat?.Login ?? modelRows.FirstOrDefault()?.UserLogin ?? daily.FirstOrDefault()?.UserLogin ?? login;

        var credits = modelRows.Sum(r => r.Credits);
        var allCredits = await GetUserCreditsAsync(m, ct);
        var activeLogins = await db.Seats.AsNoTracking().Where(s => s.IsActive).Select(s => s.Login).ToListAsync(ct);
        var ranking = Leaderboard.Rank(
            activeLogins.Union(allCredits.Keys, StringComparer.OrdinalIgnoreCase).Select(l => new UserCredits(l, allCredits.GetValueOrDefault(l))),
            CreditsPerSeat, 0m);
        var rank = ranking.FirstOrDefault(r => string.Equals(r.Login, canonical, StringComparison.OrdinalIgnoreCase))?.Rank ?? 0;

        // Daily series, zero-filled up to today (or the whole month if it is over).
        var byDay = daily.ToDictionary(d => d.Day, d => d.AiCredits);
        var lastDay = today < m.Start ? m.Start.AddDays(-1) : today > m.LastDay ? m.LastDay : today;
        var series = new List<DailyCredit>();
        decimal running = 0m;
        for (var d = m.Start; d <= lastDay; d = d.AddDays(1))
        {
            var c = byDay.GetValueOrDefault(d);
            running += c;
            series.Add(new DailyCredit(d, c, running));
        }

        var byModel = modelRows
            .Select(r => new UserModelCredit(r.Model, r.Credits, r.Amount, PoolCalculator.PercentOf(r.Credits, credits)))
            .OrderByDescending(x => x.Credits).ThenBy(x => x.Model, StringComparer.Ordinal).ToList();

        var active = daily.Where(d => d.Prompts > 0 || d.SuggestionsGenerated > 0 || d.AiCredits > 0).ToList();
        var generated = daily.Sum(d => d.SuggestionsGenerated);
        var accepted = daily.Sum(d => d.SuggestionsAccepted);
        var productivity = new ProductivityTotals(
            daily.Sum(d => d.Prompts), generated, accepted,
            generated > 0 ? (decimal)accepted / generated : null,
            daily.Sum(d => d.LocSuggestedToAdd), daily.Sum(d => d.LocAdded), daily.Sum(d => d.LocDeleted),
            active.Count, daily.Count(d => d.UsedChat), daily.Count(d => d.UsedAgent),
            active.Count > 0 ? active.Max(d => d.Day) : null);

        var (seatCount, _) = await GetSeatCountsAsync(ct);
        var poolLimit = PoolCalculator.Limit(seatCount, CreditsPerSeat);

        return new UserDetail(
            m, canonical, seat?.AvatarUrl, seat?.AssigningTeam, seat?.Organization,
            SeatOrganizations.HasMany(await db.Seats.AsNoTracking().Where(s => s.IsActive).Select(s => s.Organization).ToListAsync(ct)),
            seat?.IsActive ?? false,
            seat?.LastActivityAt, seat?.LastActivityEditor,
            credits, PoolCalculator.ToDollars(credits), CreditsPerSeat,
            Leaderboard.FairSharePercent(credits, CreditsPerSeat),
            rank, ranking.Count,
            daily.Sum(d => d.AiCredits), series, byModel,
            Activity(breakdowns, MetricDimension.Feature),
            Activity(breakdowns, MetricDimension.Ide),
            Activity(breakdowns, MetricDimension.Client),
            Activity(breakdowns, MetricDimension.Language),
            Activity(breakdowns, MetricDimension.Model),
            productivity,
            poolLimit, PoolCalculator.PercentOf(credits, poolLimit));
    }

    /// <summary>
    /// True when <see cref="GetUserDetailAsync"/> would find the login (a seat row, or billing or metrics rows this month).
    /// Cheap enough to run before serving a page, so an unknown login can answer with a 404.
    /// </summary>
    public async Task<bool> UserExistsAsync(string login, BillingMonth? month = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(login)) return false;
        var m = month ?? CurrentMonth;
        return await db.Seats.AnyAsync(s => s.Login == login, ct)
            || await db.UserModelMonthly.AnyAsync(x => x.UserLogin == login && x.MonthStart == m.Start, ct)
            || await db.UserMetricDaily.AnyAsync(x => x.UserLogin == login && x.Day >= m.Start && x.Day < m.NextStart, ct);
    }

    public Task<SyncInfo?> GetLastSyncAsync(CancellationToken ct = default) => GetLatestSyncAsync(ct);

    /// <summary>The newest sync run that finished with every step succeeded (a partial failure does not count). Also proves the database is readable.</summary>
    public async Task<SyncInfo?> GetLastFullySuccessfulSyncAsync(CancellationToken ct = default)
    {
        var r = await db.SyncRuns.AsNoTracking().Where(x => x.Status == SyncStatus.Succeeded).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return r is null ? null : new SyncInfo(r.Id, r.StartedAt, r.FinishedAt, r.Status.ToString(), r.IsDemo, r.Error);
    }

    // ---- helpers ----

    private static IReadOnlyList<ActivityItem> Activity(IEnumerable<UserMetricBreakdown> rows, MetricDimension dimension) =>
        rows.Where(r => r.Dimension == dimension)
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var gen = g.Sum(r => r.Generations);
                var acc = g.Sum(r => r.Acceptances);
                return new ActivityItem(
                    g.Key, g.Sum(r => r.Interactions), gen, acc, gen > 0 ? (decimal)acc / gen : null,
                    g.Sum(r => r.LocAdded), g.Sum(r => r.LocDeleted), g.Sum(r => r.Requests), g.Sum(r => r.Tokens),
                    g.OrderByDescending(r => r.Day).Select(r => r.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));
            })
            .OrderByDescending(a => a.Interactions + a.Requests + a.Generations)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>Billed credits per user for the month (from the per-user per-model rows).</summary>
    private async Task<Dictionary<string, decimal>> GetUserCreditsAsync(BillingMonth m, CancellationToken ct) =>
        (await db.UserModelMonthly.AsNoTracking().Where(x => x.MonthStart == m.Start).ToListAsync(ct))
            .GroupBy(x => x.UserLogin, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Credits), StringComparer.OrdinalIgnoreCase);

    /// <summary>Total seats (billing is per assigned seat) and active-this-cycle, from the newest snapshot; falls back to counting seat rows.</summary>
    private async Task<(int Seats, int ActiveThisCycle)> GetSeatCountsAsync(CancellationToken ct)
    {
        var snap = await db.SeatSnapshots.AsNoTracking().OrderByDescending(s => s.Day).FirstOrDefaultAsync(ct);
        if (snap is not null) return (snap.TotalSeats, snap.ActiveThisCycle);
        var count = await db.Seats.CountAsync(s => s.IsActive, ct);
        return (count, count);
    }

    private async Task<SyncInfo?> GetLatestSyncAsync(CancellationToken ct, bool successfulOnly = false)
    {
        var q = db.SyncRuns.AsNoTracking().AsQueryable();
        if (successfulOnly) q = q.Where(r => r.Status == SyncStatus.Succeeded || r.Status == SyncStatus.PartialFailure);
        var r = await q.OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return r is null ? null : new SyncInfo(r.Id, r.StartedAt, r.FinishedAt, r.Status.ToString(), r.IsDemo, r.Error);
    }
}
