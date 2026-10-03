using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Per-account settings, as an open key/value store.
///
/// Open rather than a fixed schema on purpose. The client's settings surface is built from a table
/// this server cannot see, and a client that adds a setting would get a 404 for its own field if the
/// schema were fixed here. Unknown keys are stored and echoed unchanged, which means a newer client
/// works against this server without a server change.
/// </summary>
public static class PlayerSettingsEndpoints
{
    /// <summary>HttpContext.Items key for the parsed body, shared with the other settings readers.</summary>
    private const string BodyCacheKey = "recemu.settingsBody";

    public static void Map(WebApplication app)
    {
        app.MapGet("/account/me/settings", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var prefix = (await http.ReadStringAsync("prefix")) ?? string.Empty;

            var rows = await db.PlayerSettings.AsNoTracking()
                .Where(s => s.PlayerId == caller.Id && (prefix.Length == 0 || s.Key.StartsWith(prefix)))
                .ToListAsync(ct);

            return Results.Json(rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal), Json.Options);
        });

        app.MapMethods("/account/me/settings", ["PUT", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var pairs = await ReadPairsAsync(http);
            if (pairs.Count == 0) return Bare.Ok();

            var keys = pairs.Keys.ToList();
            var existing = await db.PlayerSettings
                .Where(s => s.PlayerId == caller.Id && keys.Contains(s.Key))
                .ToListAsync(ct);

            var byKey = existing.ToDictionary(s => s.Key, StringComparer.Ordinal);

            foreach (var (key, value) in pairs)
            {
                if (!byKey.TryGetValue(key, out var row))
                {
                    row = new PlayerSetting { PlayerId = caller.Id, Key = key };
                    db.PlayerSettings.Add(row);
                    byKey[key] = row;
                }

                row.Value = value;
            }

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/account/{accountId:int}/settings", async (int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var rows = await db.PlayerSettings.AsNoTracking()
                .Where(s => s.PlayerId == accountId)
                .ToListAsync(ct);
            return Results.Json(rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal), Json.Options);
        });

        app.MapDelete("/account/me/settings/{key}", async (
            string key, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var row = await db.PlayerSettings
                .FirstOrDefaultAsync(s => s.PlayerId == caller.Id && s.Key == key, ct);
            if (row is null) return Bare.Ok();

            db.PlayerSettings.Remove(row);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        // The newer v2 surface the client's settings screen uses. Same storage, different envelope,
        // because the two generations of client expect different key spellings on the wire and
        // storing both from one place is what keeps them in agreement.
        app.MapMethods("/api/players/v4/current/settings", ["GET", "PUT", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            if (http.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var rows = await db.PlayerSettings.AsNoTracking()
                    .Where(s => s.PlayerId == caller.Id)
                    .ToListAsync(ct);
                return Results.Json(rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal), Json.Options);
            }

            var pairs = await ReadPairsAsync(http);
            var keys = pairs.Keys.ToList();
            var existing = await db.PlayerSettings
                .Where(s => s.PlayerId == caller.Id && keys.Contains(s.Key))
                .ToListAsync(ct);
            var byKey = existing.ToDictionary(s => s.Key, StringComparer.Ordinal);

            foreach (var (key, value) in pairs)
            {
                if (!byKey.TryGetValue(key, out var row))
                {
                    row = new PlayerSetting { PlayerId = caller.Id, Key = key };
                    db.PlayerSettings.Add(row);
                }

                row.Value = value;
            }

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    /// <summary>
    /// Reads settings from either a form body or a JSON object.
    ///
    /// In a form body each field *is* the setting key and its value is the setting value. The one
    /// exception is a literal <c>value</c> field, which the client sends alongside a <c>key</c> field
    /// for single-setting writes; that pair is handled separately rather than being stored under a
    /// setting literally named "value".
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadPairsAsync(HttpContext http)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync();

            if (form.TryGetValue("key", out var key) && form.TryGetValue("value", out var value))
            {
                pairs[key.ToString()] = value.ToString();
                return pairs;
            }

            foreach (var (name, raw) in form)
            {
                if (name.Equals("value", StringComparison.OrdinalIgnoreCase)) continue;
                pairs[name] = raw.ToString();
            }

            if (pairs.Count > 0) return pairs;
        }

        if (http.Request.ContentType is null ||
            !http.Request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return pairs;
        }

        var body = await ReadJsonBodyAsync(http);
        if (body is not System.Text.Json.Nodes.JsonObject obj) return pairs;

        foreach (var (key, value) in obj) pairs[key] = value?.ToString() ?? string.Empty;
        return pairs;
    }

    private static async Task<System.Text.Json.Nodes.JsonNode?> ReadJsonBodyAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(BodyCacheKey, out var cached)) return (System.Text.Json.Nodes.JsonNode?)cached;
        if (!http.Request.Body.CanSeek) return null;

        var position = http.Request.Body.Position;
        http.Request.Body.Position = 0;
        using var reader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync();
        http.Request.Body.Position = position;

        System.Text.Json.Nodes.JsonNode? node = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { node = System.Text.Json.Nodes.JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { return null; }
        }

        http.Items[BodyCacheKey] = node;
        return node;
    }
}