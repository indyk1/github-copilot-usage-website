using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CopilotUsage.Data;

/// <summary>Used by `dotnet ef` only. No real database is touched when adding migrations.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CopilotUsageDbContext>
{
    public CopilotUsageDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CopilotUsageDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
