using System.Net;
using CopilotUsage.SyncFunction;
using CopilotUsage.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Tests.Function;

public class HttpSyncApiClientTests
{
    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (HttpSyncApiClient Client, Capture Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond, string baseUrl = "https://app.example.test")
    {
        var handler = new Capture(respond);
        return (new HttpSyncApiClient(new HttpClient(handler), Options.Create(new SyncApiOptions { BaseUrl = baseUrl })), handler);
    }

    [Fact]
    public async Task Posts_to_api_sync_with_the_bearer_token_and_reads_the_summary()
    {
        var (client, handler) = Make(_ => StubHandler.Json("""{"status":"Succeeded","durationMs":4200,"runId":3,"steps":[{"step":"Seats","status":"Succeeded","rows":25,"errors":0}]}"""));
        var reply = await client.PostSyncAsync("tok", default);

        Assert.Equal(HttpMethod.Post, handler.Seen!.Method);
        Assert.Equal("https://app.example.test/api/sync", handler.Seen.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Seen.Headers.Authorization!.Scheme);
        Assert.Equal("tok", handler.Seen.Headers.Authorization.Parameter);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("Succeeded", reply.Status);
        Assert.Equal(4200, reply.DurationMs);
        Assert.Equal(["Seats=Succeeded(25 rows, 0 errors)"], reply.StepSummaries);
    }

    [Fact]
    public async Task A_base_url_with_a_trailing_slash_or_a_path_prefix_still_resolves()
    {
        var (client, handler) = Make(_ => StubHandler.Json("{}"), "https://app.example.test/");
        await client.PostSyncAsync(null, default);
        Assert.Equal("https://app.example.test/api/sync", handler.Seen!.RequestUri!.ToString());
        Assert.Null(handler.Seen.Headers.Authorization);
    }

    [Fact]
    public async Task An_error_response_is_reported_by_status_only()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var reply = await client.PostSyncAsync("tok", default);
        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.Null(reply.Status);
    }

    [Fact]
    public async Task A_garbled_json_body_does_not_throw()
    {
        var (client, _) = Make(_ => StubHandler.Json("{not json"));
        var reply = await client.PostSyncAsync("tok", default);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Null(reply.Status);
    }
}

public class FunctionWiringTests
{
    [Fact]
    public void The_runner_resolves_from_app_settings_with_the_documented_names_and_defaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SyncApi:BaseUrl"] = "https://app.example.test/",
            ["SyncApi:Scope"] = "api://00000000-0000-0000-0000-000000000000/.default",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSyncServices(config);
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<SyncRunner>());
        var o = sp.GetRequiredService<IOptions<SyncApiOptions>>().Value;
        Assert.Equal(230, o.TimeoutSeconds);
        Assert.Equal(3, o.MaxAttempts);
        Assert.Equal(15, o.InitialBackoffSeconds);
        Assert.Empty(o.Validate());
    }
}
