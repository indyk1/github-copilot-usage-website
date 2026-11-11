using CopilotUsage.Core;
using CopilotUsage.Data;
using CopilotUsage.Data.Entities;
using CopilotUsage.Data.Queries;
using CopilotUsage.GitHub;
using CopilotUsage.Web.Components;
using CopilotUsage.Web.Config;
using CopilotUsage.Web.Health;
using CopilotUsage.Web.Hosting;
using CopilotUsage.Web.Sync;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
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

// App Service terminates TLS in its front end and forwards over plain http with X-Forwarded-* headers. Its front-end
// addresses are not fixed, so no proxy list is configured; the site is only reachable through that front end.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
// Behind the proxy the app has no https port of its own to redirect to; the public one is 443.
builder.Services.AddHttpsRedirection(o => o.HttpsPort = 443);

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

    // Demo data and real data must never mix. Judged from the sync log stored in the database, not from file names.
    var origin = await DatabaseOrigin.GetAsync(db);
    if (demo && origin == DataOrigin.Real)
    {
        Console.Error.WriteLine("Refusing to start: " + DatabaseOrigin.DemoOnRealDataMessage);
        return 1;
    }
    if (!demo && origin == DataOrigin.DemoOnly)
    {
        app.Logger.LogWarning("The database holds only demo data and demo mode is off. Clearing the demo data (seats, usage and demo sync runs) so synthetic users never appear beside real ones; the first real sync refills it.");
        await DatabaseOrigin.ClearDemoAsync(db);
    }
}
if (demo)
    await app.Services.GetRequiredService<ISyncService>().RunAsync(SyncTrigger.Demo);

app.UseForwardedHeaders();
app.UseSecurityHeaders(SecurityHeaders.ResolveCsp(app.Configuration));
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    // The health probe may arrive over plain http from inside App Service; it must get an answer, not a redirect.
    app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/healthz"), b => b.UseHttpsRedirection());
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

// Reachable without any other check; reports only database reachability and sync times (see HealthReporter).
app.MapGet("/healthz", async (HttpContext ctx, UsageQueries queries, IOptions<SyncOptions> sync, IOptions<DemoOptions> demoOptions, TimeProvider time) =>
{
    var report = await HealthReporter.BuildAsync(queries, sync.Value, demoOptions.Value, time, ctx.RequestAborted);
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(report, statusCode: report.IsServing ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).DisableAntiforgery();

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
