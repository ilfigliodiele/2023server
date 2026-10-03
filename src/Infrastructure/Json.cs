using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RecEmu.Server.Infrastructure;

/// <summary>
/// Serialization rules that match the client's readers.
///
/// The 2023 client registers every DTO property under exactly three byte keys — PascalCase,
/// camelCase and all-lowercase — and matches case-sensitively against those three only. A fourth
/// spelling is silently dropped, and because a missing key just leaves the CLR default, a wrong
/// casing never errors: the feature is simply absent. So camelCase output (which the client
/// always registers) is the safe default, and <see cref="Aliases"/> exists for the handful of
/// properties whose Pascal spelling is not the camel spelling of the same word (UserName, PFP…)
/// where a single casing choice cannot satisfy both readers.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Write(object? value) => JsonSerializer.Serialize(value, Options);

    public static string WritePretty(object? value) => JsonSerializer.Serialize(value, Indented);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Extra key spellings merged into an object alongside its canonical keys. Unknown keys are
    /// skipped by the client's readers, so adding a key is inert; removing the wrong one is not.
    /// </summary>
    public static Dictionary<string, object?> WithAliases(
        Dictionary<string, object?> obj,
        params (string Key, object? Value)[] aliases)
    {
        foreach (var (key, value) in aliases) obj[key] = value;
        return obj;
    }

    /// <summary>An empty JSON array, for the many routes the client reads as List&lt;T&gt;.</summary>
    public static object[] Empty() => Array.Empty<object>();
}
