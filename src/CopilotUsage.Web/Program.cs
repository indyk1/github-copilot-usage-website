using CopilotUsage.Core;
using CopilotUsage.Data;
using CopilotUsage.Data.Entities;
using CopilotUsage.Data.Queries;
using CopilotUsage.GitHub;
using CopilotUsage.Web.Components;
using CopilotUsage.Web.Config;
using CopilotUsage.Web.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var configErrors = StartupConfigValidator.Validate(builder.Configuration);
if (configErrors.Count > 0)
{
    Console.Error.WriteLine("Configuration is invalid, refusing to start:");
    foreach (var e in configErrors) Console.Error.WriteLine("  - " + e);
    return 1;
}

builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection(GitHubOptions.SectionName));
builder.Services.Configure<CopilotOptions>(builder.Configuration.GetSection(CopilotOptions.SectionName));
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCopilotUsageData(ResolveConnectionString(builder));
builder.Services.AddGitHubClient();
builder.Services.AddSingleton<ISyncService, SyncService>();
builder.Services.AddHostedService<SyncWorker>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

var demo = app.Configuration.GetSection(DemoOptions.SectionName).Get<DemoOptions>()?.Enabled == true;
if (demo)
    app.Logger.LogInformation("Demo mode (scenario: {Scenario}): GitHub is not contacted; the database is filled with synthetic data.",
        app.Configuration["Demo:Scenario"] ?? "normal");
else
    app.Logger.LogInformation("Live mode: syncing from GitHub organization {Org}.", app.Configuration["GitHub:Org"]);

// Migrate, then (demo only) seed before serving so the first page view already has data.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CopilotUsageDbContext>();
    await db.Database.MigrateAsync();
    // Azure App Service keeps SQLite on a network share: stay in the default rollback journal, not WAL.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
}
if (demo)
    await app.Services.GetRequiredService<ISyncService>().RunAsync(SyncTrigger.Demo);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

// The user page renders in the browser over the circuit, so the server cannot see a missing login from the page itself.
// Answer a direct page request for an unknown login with a real 404 (the status code pages above show the not-found page).
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsGet(ctx.Request.Method)
        && ctx.Request.Path.StartsWithSegments("/users", out var rest)
        && rest.Value?.Trim('/') is { Length: > 0 } login && !login.Contains('/')
        && ctx.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
    {
        bool exists;
        try { exists = await ctx.RequestServices.GetRequiredService<UsageQueries>().UserExistsAsync(login, ct: ctx.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Logger.LogError(ex, "Could not check whether {Login} exists; serving the page so it can show its own error.", login);
            exists = true;
        }
        if (!exists)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }
    await next();
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;

// Relative SQLite paths are resolved against the content root (not the shell's cwd); the folder is created if missing.
static string ResolveConnectionString(WebApplicationBuilder b)
{
    var csb = new SqliteConnectionStringBuilder(b.Configuration.GetConnectionString("Default"));
    if (!string.IsNullOrWhiteSpace(csb.DataSource) && csb.DataSource != ":memory:" && !csb.DataSource.StartsWith("file:", StringComparison.Ordinal))
    {
        csb.DataSource = Path.GetFullPath(csb.DataSource, b.Environment.ContentRootPath);
        var dir = Path.GetDirectoryName(csb.DataSource);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
    return csb.ToString();
}

public partial class Program;
