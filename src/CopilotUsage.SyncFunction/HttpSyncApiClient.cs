using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CopilotUsage.SyncFunction;

/// <summary>POSTs to /api/sync. Reads only the small summary the web app returns; the body of an error response is never read or logged.</summary>
public sealed class HttpSyncApiClient(HttpClient http, IOptions<SyncApiOptions> options) : ISyncApiClient
{
    public async Task<SyncApiReply> PostSyncAsync(string? bearerToken, CancellationToken ct)
    {
        var baseUrl = new Uri(options.Value.BaseUrl!.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "api/sync"));
        if (bearerToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        string? status = null;
        int? duration = null;
        var steps = new List<string>();
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (body.TryGetProperty("status", out var s)) status = s.GetString();
                if (body.TryGetProperty("durationMs", out var d) && d.TryGetInt32(out var ms)) duration = ms;
                if (body.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var step in arr.EnumerateArray())
                        steps.Add($"{step.GetProperty("step").GetString()}={step.GetProperty("status").GetString()}({step.GetProperty("rows").GetInt32()} rows, {step.GetProperty("errors").GetInt32()} errors)");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // an unreadable summary is not a failure of the sync itself
            }
        }
        return new SyncApiReply(response.StatusCode, status, duration, steps);
    }
}
