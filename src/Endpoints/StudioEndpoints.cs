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
/// The creation studio: rooms authored in-game, plus inventions and playlists.
///
/// Studio-authored rooms go in the same <c>Room</c> table as the seeded ones. A separate table would
/// mean two code paths for browsing, matchmaking and detail reads, and the difference between a
/// studio room and a seeded room is only where the scene bytes came from — which is exactly the
/// <c>UnityAssetId</c> field.
/// </summary>
public static class StudioEndpoints
{
    public static void Map(WebApplication app)
    {
        MapCapabilities(app);
        MapCreation(app);
        MapInventions(app);
        MapPlaylists(app);
    }

    /// <summary>
    /// Upload capability probe. The client asks before sending a scene, and an absent route here is
    /// what makes it fall back to its built-in upload path rather than failing outright.
    /// </summary>
    private static void MapCapabilities(WebApplication app)
    {
        app.MapGet("/studio/v1/capabilities", () => Results.Json(Obj.Create(
            ("SupportsScreens", true),
            ("SupportsInventions", true),
            ("MaxUploadBytes", 268435456L)), Json.Options));
    }

    private static void MapCreation(WebApplication app)
    {
        // Creates the room and its first subroom together. A room with no subroom cannot be
        // matchmade into — matchmaking resolves a subroom before anything else — so an empty room
        // would appear in listings and then fail to open.
        app.MapMethods("/rooms", ["POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, RecEmuOptionsAccessor options,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var room = new Room
            {
                Name = ((await http.ReadStringAsync("name")) ?? $"{caller.Username}'s Room").Trim(),
                Description = await http.ReadStringAsync("description") ?? string.Empty,
                ImageName = await http.ReadStringAsync("imageName") ?? "none",
                CreatorAccountId = caller.Id,
                Accessibility = await http.ReadIntAsync("accessibility") ?? Enums.Accessibility.Public,
                MaxPlayers = await http.ReadIntAsync("maxPlayers") ?? options.Value.DefaultRoomCapacity,
                MinLevel = await http.ReadIntAsync("minLevel") ?? 0,
                SupportsJuniors = await http.ReadBoolAsync("supportsJuniors") ?? true,
                SupportsTeleportVR = await http.ReadBoolAsync("supportsTeleportVR") ?? true,
                SupportsWalkVR = await http.ReadBoolAsync("supportsWalkVR") ?? true,
                MaxPlayerCalculationMode = await http.ReadIntAsync("maxPlayerCalculationMode")
                    ?? Enums.MaxPlayerCalculationMode.Constant,
            };

            room.SubRooms.Add(new SubRoom
            {
                Name = await http.ReadStringAsync("subRoomName") ?? "Spawn",
                MaxPlayers = room.MaxPlayers,
                Accessibility = Enums.Accessibility.Public,
                IsSpawnPoint = true,
            });

            db.Rooms.Add(room);
            await db.SaveChangesAsync(ct);

            db.Creations.Add(new Creation
            {
                Type = "room",
                CreatorAccountId = caller.Id,
                Name = room.Name,
                ImageName = room.ImageName,
            });

            db.Stats.Add(new StatRecord { StatName = "rooms.built", PlayerId = caller.Id, Value = 1 });
            await db.SaveChangesAsync(ct);

            await notifications.BroadcastAsync(NotificationId.RoomUpdate,
                Obj.Create(("RoomId", room.Id), ("Name", room.Name)), ct);

            return Results.Json(Wire.RoomDetails(room), Json.Options);
        });

        app.MapGet("/studio/creations/me", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var rows = await db.Creations.AsNoTracking()
                .Where(c => c.CreatorAccountId == caller.Id)
                .OrderByDescending(c => c.Id)
                .Take(100)
                .ToListAsync(ct);

            return Results.Json(rows.Select(CreationRow).ToList(), Json.Options);
        });

        app.MapGet("/studio/creations/{creationId:long}", async (long creationId, RecEmuDb db, CancellationToken ct) =>
        {
            var creation = await db.Creations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == creationId, ct);
            return creation is null ? Results.NotFound() : Results.Json(CreationRow(creation), Json.Options);
        });

        app.MapPut("/studio/creations/{creationId:long}", async (
            HttpContext http, long creationId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var creation = await db.Creations.FirstOrDefaultAsync(c => c.Id == creationId, ct);
            if (creation is null) return Results.NotFound();
            if (creation.CreatorAccountId != caller.Id && !caller.IsAdmin) return Results.Forbid();

            if (await http.HasKeyAsync("name")) creation.Name = await http.ReadStringAsync("name") ?? creation.Name;
            if (await http.HasKeyAsync("description")) creation.Description = await http.ReadStringAsync("description") ?? creation.Description;
            if (await http.HasKeyAsync("imageName")) creation.ImageName = await http.ReadStringAsync("imageName") ?? creation.ImageName;
            if (await http.HasKeyAsync("published")) creation.Published = await http.ReadBoolAsync("published") ?? creation.Published;

            await db.SaveChangesAsync(ct);
            return Results.Json(CreationRow(creation), Json.Options);
        });
    }

    private static void MapInventions(WebApplication app)
    {
        // Inventions are stored as creations with a different type rather than their own entity:
        // nothing about an invention is structurally different from a room to the routes that read
        // them, and a separate table would duplicate every one of those routes.
        app.MapMethods("/inventions", ["GET", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            if (http.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var invention = new Creation
                {
                    Type = "invention",
                    CreatorAccountId = caller.Id,
                    Name = (await http.ReadStringAsync("name")) ?? "Untitled Invention",
                    Description = await http.ReadStringAsync("description") ?? string.Empty,
                    ImageName = await http.ReadStringAsync("imageName") ?? "none",
                    DataBlob = await http.ReadStringAsync("dataBlob") ?? string.Empty,
                    TagsCsv = Wire.Csv(await http.ReadValuesAsync("tag")),
                    Published = await http.ReadBoolAsync("published") ?? false,
                };

                db.Creations.Add(invention);
                await db.SaveChangesAsync(ct);

                return Results.Json(CreationRow(invention), Json.Options);
            }

            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 25, 1, 100);

            var rows = await db.Creations.AsNoTracking()
                .Where(c => c.Type == "invention" && c.Published)
                .OrderByDescending(c => c.Id)
                .ToListAsync(ct);

            return Results.Json(Obj.Paged(rows.Skip(skip).Take(take).Select(CreationRow), rows.Count), Json.Options);
        });

        app.MapGet("/inventions/{inventionId:long}", async (long inventionId, RecEmuDb db, CancellationToken ct) =>
        {
            var invention = await db.Creations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == inventionId && c.Type == "invention", ct);
            return invention is null ? Results.NotFound() : Results.Json(CreationRow(invention), Json.Options);
        });

        app.MapDelete("/inventions/{inventionId:long}", async (
            long inventionId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var invention = await db.Creations.FirstOrDefaultAsync(c => c.Id == inventionId && c.Type == "invention", ct);
            if (invention is null) return Bare.Ok();
            if (invention.CreatorAccountId != caller.Id && !caller.IsAdmin) return Results.Forbid();

            db.Creations.Remove(invention);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapPlaylists(WebApplication app)
    {
        app.MapGet("/playlists/me", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var rows = await db.Creations.AsNoTracking()
                .Where(c => c.Type == "playlist" && c.CreatorAccountId == caller.Id)
                .OrderByDescending(c => c.Id)
                .ToListAsync(ct);

            return Results.Json(rows.Select(CreationRow).ToList(), Json.Options);
        });

        app.MapMethods("/playlists", ["POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var playlist = new Creation
            {
                Type = "playlist",
                CreatorAccountId = caller.Id,
                Name = (await http.ReadStringAsync("name")) ?? "New Playlist",
                Description = await http.ReadStringAsync("description") ?? string.Empty,
                ImageName = await http.ReadStringAsync("imageName") ?? "none",
            };

            db.Creations.Add(playlist);
            await db.SaveChangesAsync(ct);

            return Results.Json(CreationRow(playlist), Json.Options);
        });

        // A playlist's contents are the creation's tags, reused as a room-id list. Storing them in a
        // column the row already has avoids a join table for a list that is only ever read whole.
        app.MapPut("/playlists/{playlistId:long}/rooms", async (
            HttpContext http, long playlistId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var playlist = await db.Creations.FirstOrDefaultAsync(c => c.Id == playlistId && c.Type == "playlist", ct);
            if (playlist is null) return Results.NotFound();
            if (playlist.CreatorAccountId != caller.Id && !caller.IsAdmin) return Results.Forbid();

            var ids = (await http.ReadValuesAsync("roomId", "id"))
                .Select(v => long.TryParse(v, out var id) ? id : 0)
                .Where(id => id > 0)
                .ToList();

            playlist.TagsCsv = Wire.Csv(ids.Select(id => id.ToString()));
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/playlists/{playlistId:long}/rooms", async (long playlistId, RecEmuDb db, CancellationToken ct) =>
        {
            var playlist = await db.Creations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == playlistId && c.Type == "playlist", ct);
            if (playlist is null) return Results.NotFound();

            var ids = playlist.TagsCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => long.TryParse(v, out var id) ? id : 0)
                .Where(id => id > 0)
                .ToList();

            var rooms = await db.Rooms.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
            var order = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);

            return Results.Json(rooms
                .OrderBy(r => order.GetValueOrDefault(r.Id, int.MaxValue))
                .Select(Wire.RoomSummary)
                .ToList(), Json.Options);
        });
    }

    private static Dictionary<string, object?> CreationRow(Creation creation) => Obj.Create(
        ("CreationId", creation.Id),
        ("Type", creation.Type),
        ("CreatorAccountId", creation.CreatorAccountId),
        ("Name", creation.Name),
        ("Description", creation.Description),
        ("ImageName", creation.ImageName),
        ("DataBlob", creation.DataBlob),
        ("Tags", creation.TagsCsv),
        ("Published", creation.Published),
        ("CreatedAt", Wire.Iso(creation.CreatedAt)),
        ("UpdatedAt", Wire.Iso(creation.UpdatedAt)));
}