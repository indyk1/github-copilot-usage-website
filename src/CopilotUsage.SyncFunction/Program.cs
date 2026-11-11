using CopilotUsage.SyncFunction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) => services.AddSyncServices(ctx.Configuration))
    .Build();

// Say plainly, once, what is wrong with the settings instead of failing on the first timer tick.
var syncApi = host.Services.GetRequiredService<IOptions<SyncApiOptions>>().Value;
foreach (var problem in syncApi.Validate())
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotUsage.SyncFunction").LogError("Configuration problem: {Problem}", problem);

host.Run();
