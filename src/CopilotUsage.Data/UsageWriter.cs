using CopilotUsage.Core.Calculations;
using CopilotUsage.Core.Models;
using CopilotUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CopilotUsage.Data;

/// <summary>
/// Idempotent upserts shared by the real sync and the demo seeder. Running the same input twice
/// leaves the same rows. Each method saves its own changes.
/// </summary>
public sealed class UsageWriter(CopilotUsageDbContext db)
{
    public async Task UpsertSeatSummaryAsync(SeatSummary summary, DateTimeOffset now, CancellationToken ct = default)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var row = await db.SeatSnapshots.FirstOrDefaultAsync(s => s.Day == day, ct);
        if (row is null) db.SeatSnapshots.Add(row = new SeatSnapshot { Day = day });
        row.CapturedAt = now;
        row.PlanType = summary.PlanType;
        row.TotalSeats = summary.TotalSeats;
        row.AddedThisCycle = summary.AddedThisCycle;
        row.PendingInvitation = summary.PendingInvitation;
        row.PendingCancellation = summary.PendingCancellation;
        row.ActiveThisCycle = summary.ActiveThisCycle;
        row.InactiveThisCycle = summary.InactiveThisCycle;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Upserts seats by login. Seats missing from <paramref name="seats"/> are marked inactive, except
    /// that an empty list never deactivates anyone (an empty answer is more likely a bad response than an empty org).
    /// </summary>
    public async Task<int> UpsertSeatsAsync(IReadOnlyList<SeatInfo> seats, DateTimeOffset now, CancellationToken ct = default)
    {
        var existing = (await db.Seats.ToListAsync(ct)).ToDictionary(s => s.Login, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in seats)
        {
            if (!seen.Add(s.Login)) continue;
            if (!existing.TryGetValue(s.Login, out var row))
            {
                db.Seats.Add(row = new Seat { Login = s.Login, FirstSeenAt = now });
                existing[s.Login] = row;
            }
            row.AvatarUrl = s.AvatarUrl;
            row.CreatedAt = s.CreatedAt;
            row.UpdatedAt = s.UpdatedAt;
            row.PlanType = s.PlanType;
            row.AssigningTeam = s.AssigningTeam;
            row.LastActivityAt = s.LastActivityAt;
            row.LastActivityEditor = s.LastActivityEditor;
            row.IsActive = true;
            row.LastSyncedAt = now;
        }

        if (seen.Count > 0)
            foreach (var row in existing.Values.Where(r => !seen.Contains(r.Login) && r.IsActive))
                row.IsActive = false;

        await db.SaveChangesAsync(ct);
        return seen.Count;
    }

    /// <summary>Replaces the org-wide billed rows for the covered days (lines are summed per day and model).</summary>
    public async Task<int> UpsertOrgDailyCreditsAsync(
        IEnumerable<CreditUsageRecord> records, IReadOnlyCollection<DateOnly> coveredDays, CancellationToken ct = default)
    {
        if (coveredDays.Count == 0) return 0;
        var days = coveredDays.ToHashSet();
        var min = days.Min();
        var max = days.Max();

        var desired = records
            .Where(r => r.Day is { } d && days.Contains(d))
            .GroupBy(r => (Day: r.Day!.Value, r.Model))
            .ToDictionary(g => g.Key, g => (Credits: g.Sum(r => r.Credits), Amount: g.Sum(r => r.Amount)));

        var existing = (await db.CreditUsageDaily
                .Where(x => x.UserLogin == CreditUsageDaily.OrgWide && x.Day >= min && x.Day <= max)
                .ToListAsync(ct))
            .Where(x => days.Contains(x.Day))
            .ToDictionary(x => (x.Day, x.Model));

        foreach (var (key, v) in desired)
        {
            if (!existing.Remove(key, out var row))
                db.CreditUsageDaily.Add(row = new CreditUsageDaily { Day = key.Day, Model = key.Model, UserLogin = CreditUsageDaily.OrgWide });
            row.Credits = v.Credits;
            row.Amount = v.Amount;
        }
        db.CreditUsageDaily.RemoveRange(existing.Values); // models no longer reported for that day
        await db.SaveChangesAsync(ct);
        return desired.Count;
    }

    /// <summary>Replaces one user's per-model billed rows for a month.</summary>
    public async Task<int> UpsertUserModelMonthAsync(
        BillingMonth month, string login, IEnumerable<CreditUsageRecord> records, CancellationToken ct = default)
    {
        var desired = records
            .GroupBy(r => r.Model)
            .ToDictionary(g => g.Key, g => (Credits: g.Sum(r => r.Credits), Amount: g.Sum(r => r.Amount)));

        var existing = (await db.UserModelMonthly
                .Where(x => x.MonthStart == month.Start && x.UserLogin == login)
                .ToListAsync(ct))
            .ToDictionary(x => x.Model);

        foreach (var (model, v) in desired)
        {
            if (!existing.Remove(model, out var row))
                db.UserModelMonthly.Add(row = new UserModelMonthly { MonthStart = month.Start, UserLogin = login, Model = model });
            row.Credits = v.Credits;
            row.Amount = v.Amount;
        }
        db.UserModelMonthly.RemoveRange(existing.Values);
        await db.SaveChangesAsync(ct);
        return desired.Count;
    }

    /// <summary>
    /// Upserts per-user daily metrics and their breakdowns. For each (user, day) present in the input,
    /// breakdown rows that are no longer reported are removed. Other users and days are untouched.
    /// </summary>
    public async Task<int> UpsertUserMetricsAsync(IEnumerable<UserDailyMetrics> metrics, CancellationToken ct = default)
    {
        // Last occurrence wins if a report repeats a (user, day).
        var incoming = metrics
            .GroupBy(m => (m.Day, Login: m.Login.ToLowerInvariant()))
            .Select(g => g.Last())
            .ToList();
        if (incoming.Count == 0) return 0;

        var min = incoming.Min(m => m.Day);
        var max = incoming.Max(m => m.Day);

        var wasAuto = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var dailyByKey = (await db.UserMetricDaily.Where(x => x.Day >= min && x.Day <= max).ToListAsync(ct))
                .ToDictionary(x => (x.Day, x.UserLogin.ToLowerInvariant()));
            var breakdownsByUserDay = (await db.UserMetricBreakdown.Where(x => x.Day >= min && x.Day <= max).ToListAsync(ct))
                .GroupBy(x => (x.Day, x.UserLogin.ToLowerInvariant()))
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => (x.Dimension, x.Key)));

            foreach (var m in incoming)
            {
                var key = (m.Day, m.Login.ToLowerInvariant());
                if (!dailyByKey.TryGetValue(key, out var row))
                    db.UserMetricDaily.Add(row = new UserMetricDaily { Day = m.Day, UserLogin = m.Login });
                row.AiCredits = m.AiCredits;
                row.Prompts = m.Prompts;
                row.SuggestionsGenerated = m.SuggestionsGenerated;
                row.SuggestionsAccepted = m.SuggestionsAccepted;
                row.LocSuggestedToAdd = m.LocSuggestedToAdd;
                row.LocSuggestedToDelete = m.LocSuggestedToDelete;
                row.LocAdded = m.LocAdded;
                row.LocDeleted = m.LocDeleted;
                row.UsedChat = m.UsedChat;
                row.UsedAgent = m.UsedAgent;

                breakdownsByUserDay.TryGetValue(key, out var old);
                old ??= [];
                foreach (var b in m.Breakdowns.GroupBy(b => (b.Dimension, b.Key)).Select(g => g.Last()))
                {
                    if (!old.Remove((b.Dimension, b.Key), out var br))
                        db.UserMetricBreakdown.Add(br = new UserMetricBreakdown { Day = m.Day, UserLogin = m.Login, Dimension = b.Dimension, Key = b.Key });
                    br.Interactions = b.Interactions;
                    br.Generations = b.Generations;
                    br.Acceptances = b.Acceptances;
                    br.LocAdded = b.LocAdded;
                    br.LocDeleted = b.LocDeleted;
                    br.Requests = b.Requests;
                    br.Tokens = b.Tokens;
                    br.Version = b.Version;
                }
                db.UserMetricBreakdown.RemoveRange(old.Values);
            }

            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = wasAuto;
        }
        return incoming.Count;
    }
}
