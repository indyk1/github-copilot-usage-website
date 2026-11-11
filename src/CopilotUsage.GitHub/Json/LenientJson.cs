using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotUsage.GitHub.Json;

/// <summary>
/// Serializer options used for every GitHub payload. Unknown fields are ignored; scalars of the wrong
/// type become null and non-array values where an array is expected become empty lists, instead of throwing.
/// </summary>
public static class GitHubJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        o.Converters.Add(new LenientDecimal());
        o.Converters.Add(new LenientInt());
        o.Converters.Add(new LenientLong());
        o.Converters.Add(new LenientBool());
        o.Converters.Add(new LenientString());
        o.Converters.Add(new LenientListFactory());
        return o;
    }
}

internal sealed class LenientDecimal : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Number: return r.TryGetDecimal(out var d) ? d : null;
            case JsonTokenType.String:
                return decimal.TryParse(r.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
            default: r.Skip(); return null;
        }
    }
    public override void Write(Utf8JsonWriter w, decimal? v, JsonSerializerOptions o) { if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); }
}

internal sealed class LenientInt : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Number:
                if (r.TryGetInt32(out var i)) return i;
                return r.TryGetDecimal(out var d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;
            case JsonTokenType.String:
                return int.TryParse(r.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
            default: r.Skip(); return null;
        }
    }
    public override void Write(Utf8JsonWriter w, int? v, JsonSerializerOptions o) { if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); }
}

internal sealed class LenientLong : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Number:
                if (r.TryGetInt64(out var i)) return i;
                return r.TryGetDecimal(out var d) && d >= long.MinValue && d <= long.MaxValue ? (long)d : null;
            case JsonTokenType.String:
                return long.TryParse(r.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
            default: r.Skip(); return null;
        }
    }
    public override void Write(Utf8JsonWriter w, long? v, JsonSerializerOptions o) { if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); }
}

internal sealed class LenientBool : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.String: return bool.TryParse(r.GetString(), out var b) ? b : null;
            default: r.Skip(); return null;
        }
    }
    public override void Write(Utf8JsonWriter w, bool? v, JsonSerializerOptions o) { if (v is { } x) w.WriteBooleanValue(x); else w.WriteNullValue(); }
}

internal sealed class LenientString : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.String: return r.GetString();
            case JsonTokenType.Number: return r.TryGetDecimal(out var d) ? d.ToString(CultureInfo.InvariantCulture) : null;
            case JsonTokenType.True: return "true";
            case JsonTokenType.False: return "false";
            default: r.Skip(); return null;
        }
    }
    public override void Write(Utf8JsonWriter w, string? v, JsonSerializerOptions o) { if (v is null) w.WriteNullValue(); else w.WriteStringValue(v); }
}

internal sealed class LenientListFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) =>
        (JsonConverter)Activator.CreateInstance(typeof(LenientList<>).MakeGenericType(t.GetGenericArguments()[0]))!;
}

internal sealed class LenientList<T> : JsonConverter<List<T>>
{
    public override List<T>? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var list = new List<T>();
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return list; }
        var scalar = typeof(T) == typeof(string);
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            // Elements of the wrong kind (e.g. a string where an object is expected) are dropped.
            if (!scalar && r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            var item = JsonSerializer.Deserialize<T>(ref r, o);
            if (item is not null) list.Add(item);
        }
        return list;
    }
    public override void Write(Utf8JsonWriter w, List<T> v, JsonSerializerOptions o) => JsonSerializer.Serialize(w, (IEnumerable<T>)v, o);
}
