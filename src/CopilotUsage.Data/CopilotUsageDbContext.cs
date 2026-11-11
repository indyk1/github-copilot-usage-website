using CopilotUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CopilotUsage.Data;

public class CopilotUsageDbContext(DbContextOptions<CopilotUsageDbContext> options) : DbContext(options)
{
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<SeatSnapshot> SeatSnapshots => Set<SeatSnapshot>();
    public DbSet<CreditUsageDaily> CreditUsageDaily => Set<CreditUsageDaily>();
    public DbSet<UserModelMonthly> UserModelMonthly => Set<UserModelMonthly>();
    public DbSet<UserMetricDaily> UserMetricDaily => Set<UserMetricDaily>();
    public DbSet<UserMetricBreakdown> UserMetricBreakdown => Set<UserMetricBreakdown>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Logins are matched case-insensitively by GitHub; keep them unique regardless of case.
        b.Entity<Seat>(e =>
        {
            e.ToTable("Seat");
            e.Property(x => x.Login).UseCollation("NOCASE");
            e.HasIndex(x => x.Login).IsUnique();
        });
        b.Entity<SeatSnapshot>(e =>
        {
            e.ToTable("SeatSnapshot");
            e.HasIndex(x => x.Day).IsUnique();
        });
        b.Entity<CreditUsageDaily>(e =>
        {
            e.ToTable("CreditUsageDaily");
            e.Property(x => x.UserLogin).UseCollation("NOCASE");
            e.HasIndex(x => new { x.Day, x.UserLogin, x.Model }).IsUnique();
        });
        b.Entity<UserModelMonthly>(e =>
        {
            e.ToTable("UserModelMonthly");
            e.Property(x => x.UserLogin).UseCollation("NOCASE");
            e.HasIndex(x => new { x.MonthStart, x.UserLogin, x.Model }).IsUnique();
        });
        b.Entity<UserMetricDaily>(e =>
        {
            e.ToTable("UserMetricDaily");
            e.Property(x => x.UserLogin).UseCollation("NOCASE");
            e.HasIndex(x => new { x.Day, x.UserLogin }).IsUnique();
            e.HasIndex(x => x.UserLogin);
        });
        b.Entity<UserMetricBreakdown>(e =>
        {
            e.ToTable("UserMetricBreakdown");
            e.Property(x => x.UserLogin).UseCollation("NOCASE");
            e.Property(x => x.Dimension).HasConversion<string>();
            e.HasIndex(x => new { x.Day, x.UserLogin, x.Dimension, x.Key }).IsUnique();
            e.HasIndex(x => x.UserLogin);
        });
        b.Entity<SyncRun>(e =>
        {
            e.ToTable("SyncRun");
            e.Property(x => x.Trigger).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
        });
    }
}
