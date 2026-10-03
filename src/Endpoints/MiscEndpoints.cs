using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// The services that have routes but no subsystem of their own: strings and localisation, CMS,
/// geo, data collection, platform notifications, videos, cards, lists, AI and the remaining stubs.
///
/// They are collected in one file deliberately. Each is a handful of routes returning small
/// structures, and giving each its own file and namespace would be more navigation than the code is
/// worth. What matters is that they return *shaped* answers rather than empty ones — a client that
/// reads <c>strings/v1</c> as a dictionary gets an empty dictionary and renders the whole UI in the
/// fallback language, which looks like a broken client rather than an empty server.
/// </summary>
public static class MiscEndpoints
{
    public static void Map(WebApplication app)
    {
        MapStrings(app);
        MapCms(app);
        MapGeo(app);
        MapData(app);
        MapVideos(app);
        MapNotifications(app);
        MapStubs(app);
    }

    private static void MapStrings(WebApplication app)
    {
        // Localisation. The keys the client asks for are not knowable from here, so the store
        // returns everything it has and the client falls back per-key. Returning 404 here instead
        // would make the client treat the whole table as missing and show raw key names.
        app.MapMethods("/strings/v1", ["GET", "PUT", "POST"], async (
            HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            if (http.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var rows = await db.ServerSettings.AsNoTracking()
                    .Where(s => s.Key.StartsWith("string."))
                    .ToListAsync(ct);
                return Results.Json(rows.ToDictionary(r => r.Key["string.".Length..], r => r.Value, StringComparer.Ordinal), Json.Options);
            }

            var prefix = await http.ReadStringAsync("prefix") ?? "string.";
            var keys = (await http.ReadValuesAsync("key")).ToList();
            var existing = await db.ServerSettings.Where(s => keys.Contains(s.Key)).ToListAsync(ct);
            var byKey = existing.ToDictionary(s => s.Key, StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var value = await http.ReadStringAsync(key);
                if (value is null) continue;

                if (!byKey.TryGetValue(key, out var row))
                {
                    row = new ServerSetting { Key = key };
                    db.ServerSettings.Add(row);
                    byKey[key] = row;
                }

                row.Value = value;
            }

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/api/strings/v1/bundle", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rows = await db.ServerSettings.AsNoTracking()
                .Where(s => s.Key.StartsWith("string."))
                .ToListAsync(ct);

            return Results.Json(Obj.Create(
                ("Version", 1),
                ("Strings", rows.ToDictionary(r => r.Key["string.".Length..], r => r.Value, StringComparer.Ordinal))), Json.Options);
        });
    }

    private static void MapCms(WebApplication app)
    {
        // The client's news/motd feed. An empty array renders as "no announcements", which is the
        // correct appearance for a server that has none, rather than the error state.
        app.MapGet("/cms/v1/posts", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rows = await db.Announcements.AsNoTracking()
                .OrderByDescending(a => a.Pinned)
                .ThenByDescending(a => a.Id)
                .Take(20)
                .ToListAsync(ct);

            return Results.Json(rows.Select(Wire.AnnouncementRow).ToList(), Json.Options);
        });

        app.MapGet("/cms/v1/announcements", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rows = await db.ServerSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == "cms.motd", ct);
            return Results.Json(new List<object?> { new { Title = "RecEmu", Body = rows?.Value ?? string.Empty } }, Json.Options);
        });
    }

    private static void MapGeo(WebApplication app)
    {
        // The client sends this to pick a CDN edge. The answer is advisory: it compares the value
        // against its own and falls back, so a wrong region costs latency rather than correctness.
        app.MapGet("/geo/v1/region", (HttpContext http) =>
        {
            var header = http.Request.Headers["CF-IPCountry"].ToString();
            var region = header.Length == 2 ? header.ToLowerInvariant() : "us";
            return Results.Json(Obj.Create(("Region", region)), Json.Options);
        });

        app.MapGet("/geo/v1/regions", () => Results.Json(Obj.Create(
            ("Regions", new List<object?> { "us", "eu", "ap" })), Json.Options));
    }

    private static void MapData(WebApplication app)
    {
        // Telemetry the client posts and nothing reads. Accepted and discarded rather than stored:
        // keeping crash dumps and event logs forever is a liability, and this server has no use for
        // them. The 200 is what stops the client retrying.
        app.MapMethods("/data/v1/upload", ["POST", "PUT"], () => Bare.Ok());

        app.MapMethods("/data/v1/batch", ["POST", "PUT"], () => Bare.Ok());

        app.MapGet("/gamelogs/v1/me", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var rows = await db.ServerSettings.AsNoTracking()
                .Where(s => s.Key.StartsWith($"gamelog.{caller.Id}."))
                .ToListAsync(ct);

            return Results.Json(rows.Select(s => Obj.Create(
                ("Name", s.Key[(s.Key.LastIndexOf('.') + 1)..]),
                ("Value", s.Value))).ToList(), Json.Options);
        });
    }

    private static void MapVideos(WebApplication app)
    {
        app.MapGet("/videos/me", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var rows = await db.Videos.AsNoTracking()
                .Where(v => v.CreatorAccountId == caller.Id)
                .OrderByDescending(v => v.Id)
                .ToListAsync(ct);

            return Results.Json(rows.Select(VideoRow).ToList(), Json.Options);
        });

        app.MapPost("/videos", async (HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var video = new VideoRecord
            {
                Name = (await http.ReadStringAsync("name")) ?? "Untitled",
                CreatorAccountId = caller.Id,
                Description = await http.ReadStringAsync("description") ?? string.Empty,
                ImageName = await http.ReadStringAsync("imageName") ?? "none",
                LengthSeconds = await http.ReadIntAsync("lengthSeconds") ?? 0,
            };

            db.Videos.Add(video);
            await db.SaveChangesAsync(ct);

            return Results.Json(VideoRow(video), Json.Options);
        });

        app.MapDelete("/videos/{videoId:long}", async (
            long videoId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct);
            if (video is null) return Bare.Ok();
            if (video.CreatorAccountId != caller.Id && !caller.IsAdmin) return Results.Forbid();

            db.Videos.Remove(video);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapNotifications(WebApplication app)
    {
        // Platform notifications are push messages the operator composes for every account. Kept as
        // an append-only log so the console can show what was sent.
        app.MapPost("/notifications/v1/send", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!caller.IsAdmin && !caller.IsModerator) return Results.Forbid();

            var message = (await http.ReadStringAsync("message")) ?? string.Empty;
            if (message.Length == 0) return Results.BadRequest();

            var ids = (await http.ReadValuesAsync("accountId", "id"))
                .Select(v => int.TryParse(v, out var id) ? id : 0)
                .Where(id => id > 0)
                .ToList();

            var payload = Obj.Create(("Message", message), ("SenderAccountId", caller.Id));
            if (ids.Count > 0) await notifications.SendToPlayersAsync(ids, NotificationId.AccountUpdate, payload, ct);
            else await notifications.BroadcastAsync(NotificationId.AccountUpdate, payload, ct);

            await db.ServerSettings.AddAsync(new ServerSetting
            {
                Key = $"notification.{DateTime.UtcNow:O}",
                Value = message,
            }, ct);
            await db.SaveChangesAsync(ct);

            return Bare.Ok();
        });

        app.MapGet("/notifications/v1/me", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var rows = await db.ServerSettings.AsNoTracking()
                .Where(s => s.Key.StartsWith("notification."))
                .OrderByDescending(s => s.Key)
                .Take(50)
                .ToListAsync(ct);

            return Results.Json(rows.Select(s => Obj.Create(
                ("Message", s.Value),
                ("SentAt", s.Key["notification.".Length..]))).ToList(), Json.Options);
        });

        app.MapGet("/platformnotifications/v1/settings", () => Results.Json(Obj.Create(
            ("Enabled", false),
            ("Categories", new List<object?>())), Json.Options));
    }

    /// <summary>
    /// Services with no behaviour of their own. Each returns a shaped empty answer rather than a
    /// 404, because the client treats a 404 as a service being unavailable — which disables the
    /// feature and shows the player an error — while an empty answer is indistinguishable from
    /// "nothing to show".
    /// </summary>
    private static void MapStubs(WebApplication app)
    {
        app.MapGet("/cards/v1/me", () => Results.Json(new List<object?>(), Json.Options));
        app.MapGet("/cards/v1/available", () => Results.Json(new List<object?>(), Json.Options));

        app.MapGet("/lists/v1/me", () => Results.Json(new List<object?>(), Json.Options));
        app.MapGet("/lists/v1/lists", () => Results.Json(new List<object?>(), Json.Options));

        app.MapGet("/ai/v1/chat/status", () => Bare.Bool(false));
        app.MapPost("/ai/v1/chat", () => Results.Json(Obj.Create(("Response", string.Empty)), Json.Options));

        app.MapGet("/link/v1/shorten", async (HttpContext http) =>
            Bare.Text((await http.ReadStringAsync("url")) ?? string.Empty));

        app.MapGet("/roomieintegrations/v1/me", () => Results.Json(new List<object?>(), Json.Options));

        app.MapGet("/www/v1/motd", async (RecEmuDb db, CancellationToken ct) =>
        {
            var motd = await db.ServerSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "www.motd", ct);
            return Bare.Text(motd?.Value ?? "Welcome to RecEmu.");
        });

        app.MapGet("/api/config/v1/flags", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rows = await db.ServerSettings.AsNoTracking()
                .Where(s => s.Key.StartsWith("flag."))
                .ToListAsync(ct);
            return Results.Json(rows.ToDictionary(
                r => r.Key["flag.".Length..],
                r => string.Equals(r.Value, "true", StringComparison.OrdinalIgnoreCase),
                StringComparer.Ordinal), Json.Options);
        });

        app.MapGet("/api/rooms/v1/categories", () => Results.Json(new List<object?>(), Json.Options));
    }

    private static Dictionary<string, object?> VideoRow(VideoRecord video) => Obj.Create(
        ("VideoId", video.Id),
        ("Name", video.Name),
        ("CreatorAccountId", video.CreatorAccountId),
        ("Description", video.Description),
        ("ImageName", video.ImageName),
        ("LengthSeconds", video.LengthSeconds),
        ("CreatedAt", Wire.Iso(video.CreatedAt)));
}