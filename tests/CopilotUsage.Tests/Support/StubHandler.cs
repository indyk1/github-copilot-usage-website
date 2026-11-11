using System.Net;


namespace CopilotUsage.Tests.Support;

public sealed record RecordedRequest(Uri Uri, System.Net.Http.Headers.HttpRequestHeaders Headers);

/// <summary>Primary handler that serves canned responses and records every request.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(new RecordedRequest(request.RequestUri!, request.Headers));
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
