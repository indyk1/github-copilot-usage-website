using System.Runtime.CompilerServices;
using System.Text.Json;
using CopilotUsage.GitHub.Json;

namespace CopilotUsage.GitHub;

/// <summary>Streams newline-delimited JSON. Blank and malformed lines are skipped, never fatal.</summary>
public static class NdjsonReader
{
    public static async IAsyncEnumerable<T> ReadAsync<T>(
        Stream stream,
        Action<int, string>? onMalformedLine = null,
        [EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        using var reader = new StreamReader(stream);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            T? item;
            try
            {
                item = JsonSerializer.Deserialize<T>(line, GitHubJson.Options);
            }
            catch (JsonException ex)
            {
                onMalformedLine?.Invoke(lineNumber, ex.Message);
                continue;
            }

            if (item is null)
            {
                onMalformedLine?.Invoke(lineNumber, "line was JSON null");
                continue;
            }
            yield return item;
        }
    }

    public static async Task<List<T>> ReadAllAsync<T>(Stream stream, Action<int, string>? onMalformedLine = null, CancellationToken ct = default) where T : class
    {
        var list = new List<T>();
        await foreach (var item in ReadAsync<T>(stream, onMalformedLine, ct)) list.Add(item);
        return list;
    }
}
