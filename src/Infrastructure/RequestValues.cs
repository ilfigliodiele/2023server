using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;

namespace RecEmu.Server.Infrastructure;

/// <summary>
/// Reads a request parameter from wherever the client happened to put it.
///
/// The client builds parameters through one helper that only chooses the encoding at send time:
/// GET gets a query string, POST/PUT gets an urlencoded or multipart form body, and a handful of
/// routes set a raw JSON body instead. Every one of those reaches the server with the same
/// logical name, so binding to a single source silently yields null on the other verbs. Reading
/// all three is what keeps a route working across the client's verbs without a per-verb handler.
///
/// Repeated keys are returned as multiple values: form/query collections are fully read rather
/// than comma-joined by the framework, because the client sends list parameters by repetition
/// (id=1&amp;id=2), never as CSV.
/// </summary>
public static class RequestValues
{
    /// <summary>Enable buffering so the JSON body can be read after model binding has run.</summary>
    public static void EnableBodyBuffering(this HttpContext context)
    {
        if (!context.Request.Body.CanSeek) return;
        if (context.Request.Body.Position != 0) context.Request.Body.Position = 0;
    }

    public static async Task<IReadOnlyList<string>> ReadValuesAsync(
        this HttpContext context,
        params string[] names)
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            foreach (var name in names)
            {
                if (form.TryGetValue(name, out var fromForm) && fromForm.Count > 0)
                    return fromForm.Where(v => v is not null).Select(v => v!).ToList();
            }
        }

        foreach (var name in names)
        {
            var fromQuery = context.Request.Query[name];
            if (fromQuery.Count > 0)
                return fromQuery.Where(v => v is not null)
                    .SelectMany(v => v!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        }

        var body = await ReadJsonBodyAsync(context);
        if (body is not null)
        {
            foreach (var name in names)
            {
                if (TryReadJsonValue(body, name, out var found)) return found;
            }
        }

        return Array.Empty<string>();
    }

    public static async Task<string?> ReadStringAsync(this HttpContext context, params string[] names)
    {
        var values = await context.ReadValuesAsync(names);
        return values.Count > 0 ? values[0] : null;
    }

    public static async Task<int?> ReadIntAsync(this HttpContext context, params string[] names)
    {
        var raw = await context.ReadStringAsync(names);
        return int.TryParse(raw, out var value) ? value : null;
    }

    public static async Task<long?> ReadLongAsync(this HttpContext context, params string[] names)
    {
        var raw = await context.ReadStringAsync(names);
        return long.TryParse(raw, out var value) ? value : null;
    }

    public static async Task<bool?> ReadBoolAsync(this HttpContext context, params string[] names)
    {
        var raw = await context.ReadStringAsync(names);
        if (raw is null) return null;
        if (bool.TryParse(raw, out var value)) return value;
        if (raw == "1") return true;
        if (raw == "0") return false;
        return null;
    }

    /// <summary>
    /// Reads a value that may legitimately be the JSON literal null, which the client does send
    /// for nullable enum fields (for example SubRoomId on a join). A null there means "no value",
    /// not "absent parameter", so it must be distinguishable from a missing key.
    /// </summary>
    public static async Task<bool> HasKeyAsync(this HttpContext context, params string[] names)
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            foreach (var name in names)
                if (form.ContainsKey(name)) return true;
        }
        foreach (var name in names)
            if (context.Request.Query.ContainsKey(name)) return true;

        var body = await ReadJsonBodyAsync(context);
        if (body is not null)
            foreach (var name in names)
                if (TryReadJsonValue(body, name, out _)) return true;

        return false;
    }

    private const string BodyCacheKey = "recemu.jsonBody";

    private static async Task<JsonNode?> ReadJsonBodyAsync(HttpContext context)
    {
        if (context.Items.TryGetValue(BodyCacheKey, out var cached)) return (JsonNode?)cached;
        if (!IsJsonContentType(context)) return null;
        if (!context.Request.Body.CanSeek) return null;

        var position = context.Request.Body.Position;
        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync();
        context.Request.Body.Position = position;

        JsonNode? node = string.IsNullOrWhiteSpace(text) ? null : Json.Parse(text);
        context.Items[BodyCacheKey] = node;
        return node;
    }

    private static bool IsJsonContentType(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        return contentType is not null
               && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadJsonValue(JsonNode node, string name, out List<string> values)
    {
        values = new List<string>();
        if (node is not JsonObject obj) return false;

        foreach (var property in obj)
        {
            if (!string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            switch (property.Value)
            {
                case null:
                    values.Add(string.Empty);
                    break;
                case JsonArray array:
                    values.AddRange(array.Select(node => node?.ToString() ?? string.Empty));
                    break;
                case JsonValue value:
                    values.Add(value.ToString());
                    break;
                default:
                    values.Add(property.Value.ToJsonString());
                    break;
            }
        }
        return values.Count > 0;
    }
}
