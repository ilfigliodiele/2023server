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
/// Rooms: browsing, reading, and every mutation.
///
/// Three things in this file exist because of how the client actually calls these routes, and each
/// one is a silent failure when missed.
///
/// Route twins. The client's room client prefixes a large share of its requests with
/// <c>roomserver/</c>, and which ones it prefixes is not derivable from anything but observed
/// traffic. Registering only the bare path means editing a room's name returns 404 on this build
/// and works on another. So every room route is registered twice — see <see cref="MapRoom"/>.
///
/// Full-details responses. Every mutation returns the complete room object, not a slim one: the
/// client re-parses full details after each write and dereferences nested SubRooms, Roles, Tags and
/// Stats. A slim response throws inside a dispose path, so the room looks like it failed to save
/// even though it saved.
///
/// Form bodies. The client sends urlencoded forms everywhere, so nothing here binds a JSON body.
/// </summary>
public static class RoomEndpoints
{
    public static void Map(WebApplication app)
    {
        MapBrowsing(app);
        MapReads(app);
        MapMetadata(app);
        MapModeration(app);
        MapSubRooms(app);
    }

    /// <summary>
    /// Registers a rooms route twice: bare and under the roomserver/ prefix the client
    /// sometimes sends. Both share one handler, so the two spellings cannot drift apart.
    /// </summary>
    private static void MapRoom(WebApplication app, string suffix, Delegate handler)
    {
        var (methods, pattern) = SplitPattern(suffix);
        app.MapMethods(pattern, methods, handler);
        app.MapMethods("roomserver/" + pattern, methods, handler);
    }

    private static (string[] Methods, string Pattern) SplitPattern(string suffix)
    {
        var space = suffix.IndexOf(' ');
        return space < 0
            ? (["GET"], suffix)
            : (suffix[..space].Split(','), suffix[(space + 1)..]);
    }

    private static void MapBrowsing(WebApplication app)
    {
        app.MapGet("/api/rooms/v1/filters", async (RecEmuDb db, CancellationToken ct) =>
        {
            var tags = await db.Rooms.AsNoTracking()
                .Where(r => r.TagsCsv != string.Empty)
                .Select(r => r.TagsCsv)
                .Take(200)
                .ToListAsync(ct);

            var distinct = tags
                .SelectMany(csv => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Json(Wire.RoomFilters(distinct), Json.Options);
        });

        app.MapGet("/rooms/base", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rooms = await db.Rooms.AsNoTracking()
                .Where(r => r.State == 0)
                .OrderBy(r => r.Id)
                .Take(500)
                .ToListAsync(ct);
            return Results.Json(rooms.Select(Wire.RoomSummary).ToList(), Json.Options);
        });
        app.MapGet("/roomserver/rooms/base", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rooms = await db.Rooms.AsNoTracking()
                .Where(r => r.State == 0)
                .OrderBy(r => r.Id)
                .Take(500)
                .ToListAsync(ct);
            return Results.Json(rooms.Select(Wire.RoomSummary).ToList(), Json.Options);
        });

        app.MapGet("/rooms/bulk", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var ids = (await http.ReadValuesAsync("id")).Select(v => long.TryParse(v, out var id) ? id : 0).Where(id => id > 0).ToList();
            var names = (await http.ReadValuesAsync("name")).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            var rooms = await db.Rooms.AsNoTracking()
                .Where(r => (ids.Count > 0 && ids.Contains(r.Id)) || (names.Count > 0 && names.Contains(r.Name)))
                .ToListAsync(ct);

            return Results.Json(rooms.Select(Wire.RoomSummary).ToList(), Json.Options);
        });

        // Not a route the client calls — it browses through search/hot/base — but a bare GET on
        // /rooms returning the same list as search means an operator poking at the API sees the
        // catalogue instead of a 405.
        app.MapMethods("/rooms", ["GET"], (HttpContext http, RecEmuDb db, CancellationToken ct)
            => SearchAsync(http, db, ct));

        app.MapGet("/rooms/search", (HttpContext http, RecEmuDb db, CancellationToken ct)
            => SearchAsync(http, db, ct));

        app.MapGet("/rooms/hot", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 20, 1, 100);

            var rooms = await db.Rooms.AsNoTracking().Where(r => r.State == 0).OrderBy(r => r.Id).ToListAsync(ct);
            return Results.Json(Obj.Paged(rooms.Skip(skip).Take(take).Select(Wire.RoomSummary), rooms.Count), Json.Options);
        });

        app.MapGet("/rooms/createdby/{accountId:int}", async (int accountId, RecEmuDb db, CancellationToken ct) =>
            await RoomsOwnedByAsync(db, accountId, ct));

        app.MapGet("/rooms/createdby/me", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct)
            => await RoomsOwnedByAsync(db, (await current.RequireAsync(ct)).Id, ct));

        app.MapGet("/rooms/ownedby/{accountId:int}", async (int accountId, RecEmuDb db, CancellationToken ct)
            => await RoomsOwnedByAsync(db, accountId, ct));

        app.MapGet("/rooms/ownedby/me", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct)
            => await RoomsOwnedByAsync(db, (await current.RequireAsync(ct)).Id, ct));

        MapInteractionList(app, "/rooms/cheeredby/me", "Cheered");
        MapInteractionList(app, "/rooms/favoritedby/me", "Favorited");
        MapInteractionList(app, "/rooms/visitedby/me", "Visited");
        MapInteractionList(app, "/rooms/visitedby/{accountId:int}", "Visited");

        app.MapGet("/rooms/curated_playlists", () => Results.Json(Array.Empty<long>(), Json.Options));
        app.MapGet("/rooms/rro_ids", async (RecEmuDb db, CancellationToken ct) =>
            Results.Json(await db.Rooms.AsNoTracking().Where(r => r.IsRro).Select(r => r.Id).ToListAsync(ct), Json.Options));

        app.MapGet("/rooms/moderatedby/me", () => Results.Json(new List<object?>(), Json.Options));
        app.MapGet("/rooms/contestwinners", () => Results.Json(new List<object?>(), Json.Options));
        app.MapGet("/rooms/topcreators", async (RecEmuDb db, CancellationToken ct) =>
        {
            var top = await db.Rooms.AsNoTracking()
                .GroupBy(r => r.CreatorAccountId)
                .Select(g => new { CreatorId = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(20)
                .ToListAsync(ct);

            var creators = await db.Players.AsNoTracking()
                .Where(p => top.Select(t => t.CreatorId).Contains(p.Id))
                .ToListAsync(ct);

            return Results.Json(creators.Select(Wire.Account).ToList(), Json.Options);
        });

        app.MapGet("/rooms/fromcreators", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var ids = (await http.ReadValuesAsync("id")).Select(v => int.TryParse(v, out var id) ? id : 0).Where(id => id > 0).ToList();
            if (ids.Count == 0) return Results.Json(new List<object?>(), Json.Options);

            var rooms = await db.Rooms.AsNoTracking().Where(r => ids.Contains(r.CreatorAccountId)).ToListAsync(ct);
            return Results.Json(rooms.Select(Wire.RoomSummary).ToList(), Json.Options);
        });

        app.MapGet("/rooms/magic_door", async (RecEmuDb db, RecEmuOptionsAccessor options, CancellationToken ct) =>
        {
            var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.State == 0, ct);
            return Results.Json(Obj.Create(
                ("Room", room is null ? null : Wire.RoomSummary(room)),
                ("RefreshesAt", Wire.Iso(DateTime.UtcNow.AddHours(1))),
                ("RefreshIntervalMinutes", 60)), Json.Options);
        });

        // A seed-room group, not a flat room list: the client renders one row per group.
        app.MapGet("/rooms/recommendations", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rooms = await db.Rooms.AsNoTracking().Where(r => r.State == 0).OrderBy(r => r.Id).Take(6).ToListAsync(ct);
            var groups = rooms.Chunk(3).Select(chunk => Obj.Create(
                ("SeedRoom", chunk.FirstOrDefault() is null ? null : Wire.RoomSummary(chunk[0])),
                ("Rooms", chunk.Select(Wire.RoomSummary).ToList()))).ToList();
            return Results.Json(groups, Json.Options);
        });

        app.MapGet("/featuredrooms/current", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rooms = await db.Rooms.AsNoTracking().Where(r => r.State == 0).OrderBy(r => r.Id).Take(6).ToListAsync(ct);
            return Results.Json(Obj.Create(
                ("FeaturedRoomGroupId", 1L),
                ("Name", "Featured"),
                // Each tile needs RoomName; a plain room summary leaves it null and the caption
                // renders blank, which is why the tile has its own row shape.
                ("Rooms", rooms.Select(Wire.FeaturedRoomTile).ToList())), Json.Options);
        });
        app.MapGet("/roomserver/featuredrooms/current", async (RecEmuDb db, CancellationToken ct) =>
        {
            var rooms = await db.Rooms.AsNoTracking().Where(r => r.State == 0).OrderBy(r => r.Id).Take(6).ToListAsync(ct);
            return Results.Json(Obj.Create(
                ("FeaturedRoomGroupId", 1L),
                ("Name", "Featured"),
                ("Rooms", rooms.Select(Wire.FeaturedRoomTile).ToList())), Json.Options);
        });

        app.MapPost("/api/rooms/v1/verifyRole", async (HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var roomId = await http.ReadLongAsync("roomId") ?? 0;
            var requestedRole = await http.ReadIntAsync("role") ?? Enums.RoomRole.None;
            var context = await http.ReadStringAsync("context") ?? string.Empty;
            _ = context;

            if (!await Rooms.CanManageAsync(db, roomId, caller.Id, requestedRole, ct))
                return Results.Json(Obj.Create(("CanManageRoom", false)), Json.Options);

            return Results.Json(Obj.Create(("CanManageRoom", true)), Json.Options);
        });

        // Form body with PascalCase keys. A JSON-only handler 415s before it runs, so room reports
        // are never recorded even though the client reports success.
        app.MapPost("/api/rooms/v2/report", async (HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var roomId = await http.ReadLongAsync("RoomId", "roomId");
            var details = await http.ReadStringAsync("Details", "Message", "details") ?? string.Empty;
            var category = await http.ReadIntAsync("ReportCategory", "Category") ?? 0;
            if (roomId is null) return Bare.Ok();

            db.Reports.Add(new Report
            {
                ReporterAccountId = caller.Id,
                Category = category,
                Details = details,
                ReportedRoomId = roomId,
            });
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static async Task<IResult> SearchAsync(HttpContext http, RecEmuDb db, CancellationToken ct)
    {
        var query = (await http.ReadStringAsync("query", "name"))?.Trim() ?? string.Empty;
        var skip = await http.ReadIntAsync("skip") ?? 0;
        var take = Math.Clamp(await http.ReadIntAsync("take") ?? 20, 1, 100);

        var filtered = await db.Rooms.AsNoTracking()
            .Where(r => r.State == 0)
            .Where(r => query.Length == 0 || r.Name.Contains(query) || r.Description.Contains(query))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        return Results.Json(Obj.Paged(filtered.Skip(skip).Take(take).Select(Wire.RoomSummary), filtered.Count), Json.Options);
    }

    private static async Task<IResult> RoomsOwnedByAsync(RecEmuDb db, int accountId, CancellationToken ct)
    {
        var rooms = await db.Rooms.AsNoTracking()
            .Where(r => r.CreatorAccountId == accountId && r.State == 0)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return Results.Json(rooms.Select(Wire.RoomSummary).ToList(), Json.Options);
    }

    private static void MapInteractionList(WebApplication app, string path, string column)
    {
        app.MapGet(path, async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 100, 1, 500);

            var query = db.RoomInteractions.AsNoTracking();

            // The account is either a path parameter or implied by "me".
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var last = segments[^1];

            // "me" has to be resolved from the bearer token here. Reading it from the request
            // context items instead only works if some earlier handler on this request already
            // resolved the caller, which is true for mutations and false for these reads — the
            // list then silently comes back empty rather than 401ing.
            var accountId = last == "me"
                ? (await current.GetAsync(ct))?.Id ?? 0
                : int.TryParse(last, out var parsed) ? parsed : 0;

            if (accountId == 0) return Results.Json(new List<object?>(), Json.Options);

            query = column switch
            {
                "Cheered" => query.Where(i => i.PlayerId == accountId && i.Cheered),
                "Favorited" => query.Where(i => i.PlayerId == accountId && i.Favorited),
                _ => query.Where(i => i.PlayerId == accountId && i.LastVisitedAt != null),
            };

            var interactions = await query.OrderByDescending(i => i.Id).ToListAsync(ct);
            var rooms = await db.Rooms.AsNoTracking().Where(r => interactions.Select(i => i.RoomId).Contains(r.Id)).ToListAsync(ct);
            var order = interactions.ToDictionary(i => i.RoomId, i => i.Id);

            return Results.Json(rooms.OrderBy(r => order.GetValueOrDefault(r.Id, 0)).Skip(skip).Take(take)
                .Select(Wire.RoomSummary).ToList(), Json.Options);
        });
    }

    private static void MapReads(WebApplication app)
    {
        MapRoom(app, "GET rooms/{roomId:long}", async (long roomId, RecEmuDb db, CancellationToken ct) =>
            await DetailsAsync(roomId, db, ct));

        // Owner delete. Registering only the admin route means the client's DELETE 405s and owners
        // can never remove a room they made.
        MapRoom(app, "DELETE rooms/{roomId:long}", async (long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
            if (room is null) return Bare.Ok();
            if (room.CreatorAccountId != caller.Id && !caller.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Soft delete: instances, subroom saves and comments still reference this room, and
            // a hard delete would take a live room's history with it.
            room.State = 1;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/rooms/{roomId:long}/similar", async (long roomId, RecEmuDb db, CancellationToken ct) =>
        {
            var seed = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, ct);
            if (seed is null) return Results.NotFound();

            var others = await db.Rooms.AsNoTracking()
                .Where(r => r.State == 0 && r.Id != roomId && r.TagsCsv == seed.TagsCsv)
                .OrderBy(r => r.Id)
                .Take(6)
                .ToListAsync(ct);

            return Results.Json(Obj.Create(
                ("SeedRoom", Wire.RoomSummary(seed)),
                ("Rooms", others.Select(Wire.RoomSummary).ToList())), Json.Options);
        });

        MapRoom(app, "GET rooms/{roomId:long}/roles", async (long roomId, RecEmuDb db, CancellationToken ct) =>
        {
            var roles = await db.RoomRoles.AsNoTracking().Where(r => r.RoomId == roomId).ToListAsync(ct);
            return Results.Json(roles.Select(Wire.RoleRow).ToList(), Json.Options);
        });

        MapRoom(app, "GET rooms/{roomId:long}/roles/{accountId:int}", async (long roomId, int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var role = await db.RoomRoles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.RoomId == roomId && r.AccountId == accountId, ct);
            return role is null ? Results.NotFound() : Results.Json(Wire.RoleRow(role), Json.Options);
        });

        MapRoom(app, "GET rooms/{roomId:long}/interactionby/me", async (long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var row = await db.RoomInteractions.AsNoTracking()
                .FirstOrDefaultAsync(i => i.PlayerId == caller.Id && i.RoomId == roomId, ct);

            return Results.Json(Wire.Interaction(row?.Cheered ?? false, row?.Favorited ?? false, row?.LastVisitedAt), Json.Options);
        });
    }

    private static async Task<Dictionary<string, object?>?> DetailsOrNullAsync(long roomId, RecEmuDb db, CancellationToken ct)
    {
        var room = await db.Rooms.AsNoTracking()
            .Include(r => r.SubRooms)
            .Include(r => r.Roles)
            .FirstOrDefaultAsync(r => r.Id == roomId, ct);

        return room is null ? null : Wire.RoomDetails(room);
    }

    private static async Task<IResult> DetailsAsync(long roomId, RecEmuDb db, CancellationToken ct)
    {
        var details = await DetailsOrNullAsync(roomId, db, ct);
        return details is null ? Results.NotFound() : Results.Json(details, Json.Options);
    }

    private static void MapMetadata(WebApplication app)
    {
        MapRoom(app, "PUT rooms/{roomId:long}/name", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.Name = ((await http.ReadStringAsync("name")) ?? room.Name).Trim();
            await db.SaveChangesAsync(ct);
            await AnnounceRoomAsync(notifications, room, ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/description", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.Description = (await http.ReadStringAsync("description")) ?? string.Empty;
            await db.SaveChangesAsync(ct);
            await AnnounceRoomAsync(notifications, room, ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/image", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.ImageName = (await http.ReadStringAsync("imageName")) ?? room.ImageName;
            await db.SaveChangesAsync(ct);
            await AnnounceRoomAsync(notifications, room, ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // The client sends repeated `tag` and `autoTag` fields, never a CSV under "Tags", so
        // reading a single field name here would silently discard every edit.
        MapRoom(app, "PUT rooms/{roomId:long}/tags", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var tags = await http.ReadValuesAsync("tag", "Tags", "tags");
            var autoTags = await http.ReadValuesAsync("autoTag", "AutoTags", "autoTags");
            room.TagsCsv = Wire.Csv(tags);
            room.AutoTagsCsv = Wire.Csv(autoTags);
            await db.SaveChangesAsync(ct);
            await AnnounceRoomAsync(notifications, room, ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/warning", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.WarningMask = await http.ReadIntAsync("warningMask", "RoomWarningMask") ?? room.WarningMask;
            room.CustomWarning = (await http.ReadStringAsync("customWarning", "CustomRoomWarning")) ?? room.CustomWarning;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // Four separate booleans, not one. Reading a single field loses three of the four settings.
        MapRoom(app, "PUT rooms/{roomId:long}/restrictions", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.SupportsJuniors = await http.ReadBoolAsync("supportsJuniors") ?? room.SupportsJuniors;
            room.SupportsScreens = await http.ReadBoolAsync("supportsScreens") ?? room.SupportsScreens;
            room.SupportsTeleportVR = await http.ReadBoolAsync("supportsTeleportVR") ?? room.SupportsTeleportVR;
            room.SupportsWalkVR = await http.ReadBoolAsync("supportsWalkVR") ?? room.SupportsWalkVR;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/accessibility", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.Accessibility = await http.ReadIntAsync("accessibility") ?? room.Accessibility;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/min_level", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.MinLevel = Math.Max(0, await http.ReadIntAsync("minLevel") ?? room.MinLevel);
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/max_player_calculation_mode", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.MaxPlayerCalculationMode = await http.ReadIntAsync("maxPlayerCalculationMode") ?? room.MaxPlayerCalculationMode;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/cloning", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.CloningAllowed = await http.ReadBoolAsync("cloningAllowed") ?? room.CloningAllowed;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // Form key is "disable", and it inverts: true means automute is off.
        MapRoom(app, "PUT rooms/{roomId:long}/automute", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var disable = await http.ReadBoolAsync("disable", "DisableMicAutoMute") ?? room.DisableMicAutoMute;
            room.DisableMicAutoMute = disable;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/comments", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.DisableRoomComments = await http.ReadBoolAsync("disable", "DisableRoomComments") ?? room.DisableRoomComments;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/voice_chat_encryption", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.EncryptVoiceChat = await http.ReadBoolAsync("encryptVoiceChat") ?? room.EncryptVoiceChat;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/allow_new_users", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.AllowNewUsers = await http.ReadBoolAsync("allowNewUsers") ?? room.AllowNewUsers;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // The bulk settings save. All eleven keys arrive as form fields at once.
        MapRoom(app, "PUT rooms/{roomId:long}/modify", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.Name = (await http.ReadStringAsync("name")) ?? room.Name;
            room.Description = (await http.ReadStringAsync("description")) ?? room.Description;
            room.Accessibility = await http.ReadIntAsync("accessibility") ?? room.Accessibility;
            room.SupportsJuniors = await http.ReadBoolAsync("supportsJuniors") ?? room.SupportsJuniors;
            room.SupportsScreens = await http.ReadBoolAsync("supportsScreens") ?? room.SupportsScreens;
            room.SupportsTeleportVR = await http.ReadBoolAsync("supportsTeleportVR") ?? room.SupportsTeleportVR;
            room.SupportsWalkVR = await http.ReadBoolAsync("supportsWalkVR") ?? room.SupportsWalkVR;
            room.CloningAllowed = await http.ReadBoolAsync("cloningAllowed") ?? room.CloningAllowed;
            room.DisableMicAutoMute = await http.ReadBoolAsync("disableMicAutoMute") ?? room.DisableMicAutoMute;
            room.DisableRoomComments = await http.ReadBoolAsync("disableRoomComments") ?? room.DisableRoomComments;
            room.EncryptVoiceChat = await http.ReadBoolAsync("encryptVoiceChat") ?? room.EncryptVoiceChat;

            await db.SaveChangesAsync(ct);
            await AnnounceRoomAsync(notifications, room, ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/creator", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!caller.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            room.CreatorAccountId = await http.ReadIntAsync("accountId") ?? room.CreatorAccountId;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // Appends a load screen rather than replacing the list: replacing means a room can never
        // hold more than one, which is exactly what the previous behaviour did.
        MapRoom(app, "PUT rooms/{roomId:long}/loadscreen", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var screens = Json.Parse(room.LoadScreensJson) as JsonArray ?? new JsonArray();
            screens.Add(new JsonObject
            {
                ["ImageName"] = await http.ReadStringAsync("imageName") ?? "none",
                ["Title"] = await http.ReadStringAsync("title") ?? string.Empty,
                ["Subtitle"] = await http.ReadStringAsync("subtitle") ?? string.Empty,
            });
            room.LoadScreensJson = screens.ToJsonString();
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "DELETE rooms/{roomId:long}/loadscreen", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var imageName = await http.ReadStringAsync("imageName");
            var screens = Json.Parse(room.LoadScreensJson) as JsonArray ?? new JsonArray();
            var kept = new JsonArray(screens.Where(node => node?["ImageName"]?.ToString() != imageName).ToArray());
            room.LoadScreensJson = kept.ToJsonString();
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/promo_images", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var imageName = await http.ReadStringAsync("imageName");
            if (!string.IsNullOrWhiteSpace(imageName))
            {
                var images = room.PromoImagesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!images.Contains(imageName)) images.Add(imageName!);
                room.PromoImagesCsv = string.Join(',', images);
            }

            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "DELETE rooms/{roomId:long}/promo_images", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var imageName = await http.ReadStringAsync("imageName") ?? string.Empty;
            room.PromoImagesCsv = string.Join(',', room.PromoImagesCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(v => v != imageName));
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT,DELETE rooms/{roomId:long}/promo_external", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var type = await http.ReadIntAsync("type") ?? 0;
            var reference = (await http.ReadStringAsync("reference")) ?? string.Empty;

            var entries = Json.Parse(room.PromoExternalCsv) as JsonArray ?? new JsonArray();
            entries.Add(new JsonObject { ["Type"] = type, ["Reference"] = reference });
            room.PromoExternalCsv = entries.ToJsonString();
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // Per-room, per-player opaque state. The previous stub returned a fixed blob for
        // everyone, so every player loaded the same canned save.
        MapRoom(app, "GET rooms/{roomId:long}/playerdata/me", async (long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var row = await db.PlayerRoomData.AsNoTracking()
                .FirstOrDefaultAsync(d => d.PlayerId == caller.Id && d.RoomId == roomId, ct);
            return Results.Json(Wire.PlayerRoomData(row?.Data ?? SeedContent.DormSceneBlob), Json.Options);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/playerdata/me", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var data = (await http.ReadStringAsync("data")) ?? string.Empty;

            var row = await db.PlayerRoomData.FirstOrDefaultAsync(d => d.PlayerId == caller.Id && d.RoomId == roomId, ct);
            if (row is null) db.PlayerRoomData.Add(new PlayerRoomData { PlayerId = caller.Id, RoomId = roomId, Data = data });
            else row.Data = data;

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        MapRoom(app, "PUT,DELETE rooms/{roomId:long}/interactionby/me/cheer", async (long roomId, HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var row = await InteractionAsync(db, caller.Id, roomId, ct);
            row.Cheered = !http.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        MapRoom(app, "PUT,DELETE rooms/{roomId:long}/interactionby/me/favorite", async (long roomId, HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var row = await InteractionAsync(db, caller.Id, roomId, ct);
            row.Favorited = !http.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static async Task<RoomInteraction> InteractionAsync(RecEmuDb db, int playerId, long roomId, CancellationToken ct)
    {
        var row = await db.RoomInteractions.FirstOrDefaultAsync(i => i.PlayerId == playerId && i.RoomId == roomId, ct);
        if (row is null)
        {
            row = new RoomInteraction { PlayerId = playerId, RoomId = roomId };
            db.RoomInteractions.Add(row);
        }
        return row;
    }

    private static void MapModeration(WebApplication app)
    {
        MapRoom(app, "GET rooms/{roomId:long}/bans", async (long roomId, RecEmuDb db, CancellationToken ct) =>
        {
            var bans = await db.RoomBans.AsNoTracking().Where(b => b.RoomId == roomId).ToListAsync(ct);
            return Results.Json(bans.Select(Wire.BanRow).ToList(), Json.Options);
        });

        // Repeated `id` plus a `banMask`, not a JSON body.
        MapRoom(app, "POST rooms/{roomId:long}/bans", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Bare.Ok();

            var ids = (await http.ReadValuesAsync("id", "playerId"))
                .Select(v => int.TryParse(v, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var mask = await http.ReadIntAsync("banMask", "BanType") ?? Enums.BanMask.Join;

            foreach (var bannedId in ids)
            {
                if (await db.RoomBans.AnyAsync(b => b.RoomId == roomId && b.BannedPlayerId == bannedId, ct)) continue;
                db.RoomBans.Add(new RoomBan
                {
                    RoomId = roomId,
                    BannedPlayerId = bannedId,
                    BannedByPlayerId = caller.Id,
                    BanMask = mask,
                });
            }

            await db.SaveChangesAsync(ct);

            // Announced as ModerationKick with IsBan. The client registers no handler for the
            // protocol's ban event id, so sending that id drops the notification entirely.
            await notifications.BanAsync(ids, roomId, "banned from room", ct);
            return Bare.Ok();
        });

        MapRoom(app, "POST rooms/{roomId:long}/bans/import", async (HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await Rooms.CanManageAsync(db, roomId, caller.Id, Enums.RoomRole.CoOwner, ct)) return Bare.Ok();

            var sourceRoomId = await http.ReadLongAsync("sourceRoomId") ?? 0;
            if (sourceRoomId == 0) return Bare.Ok();

            var source = await db.RoomBans.AsNoTracking().Where(b => b.RoomId == sourceRoomId).ToListAsync(ct);
            foreach (var ban in source)
            {
                if (await db.RoomBans.AnyAsync(b => b.RoomId == roomId && b.BannedPlayerId == ban.BannedPlayerId, ct)) continue;
                db.RoomBans.Add(new RoomBan
                {
                    RoomId = roomId,
                    BannedPlayerId = ban.BannedPlayerId,
                    BannedByPlayerId = caller.Id,
                    BanMask = ban.BanMask,
                    Reason = ban.Reason,
                });
            }

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        MapRoom(app, "DELETE rooms/{roomId:long}/bans/{accountId:int}", async (
            HttpContext http, long roomId, int accountId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Bare.Ok();

            var ban = await db.RoomBans.FirstOrDefaultAsync(b => b.RoomId == roomId && b.BannedPlayerId == accountId, ct);
            if (ban is not null)
            {
                db.RoomBans.Remove(ban);
                await db.SaveChangesAsync(ct);
            }

            await notifications.SendToPlayerAsync(accountId, NotificationId.ModerationUnkick, Obj.Create(
                ("AccountId", accountId),
                ("RoomId", roomId)), ct);
            _ = http;
            return Bare.Ok();
        });

        MapRoom(app, "PUT rooms/{roomId:long}/roles/{accountId:int}", async (
            HttpContext http, long roomId, int accountId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var role = await http.ReadIntAsync("role") ?? Enums.RoomRole.None;
            await AssignRoleAsync(db, roomId, accountId, role, caller.Id, ct);
            await notifications.SendToPlayerAsync(accountId, NotificationId.AccountUpdate,
                Obj.Create(("AccountId", accountId), ("RoomId", roomId), ("Role", role)), ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/roles/{accountId:int}/invite", async (
            HttpContext http, long roomId, int accountId, CurrentPlayerAccessor current, RecEmuDb db,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var role = await http.ReadIntAsync("role") ?? Enums.RoomRole.None;

            var row = await db.RoomRoles.FirstOrDefaultAsync(r => r.RoomId == roomId && r.AccountId == accountId, ct);
            if (row is null) db.RoomRoles.Add(new RoomRoleAssignment
            {
                RoomId = roomId,
                AccountId = accountId,
                InvitedRole = role,
                LastChangedByAccountId = caller.Id,
            });
            else row.InvitedRole = role;

            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // Returns the clone's full details, not a status wrapper: the client parses the room out
        // of this response and shows a message-less "Failed to copy room" when it is absent.
        MapRoom(app, "POST rooms/{roomId:long}/clone", async (
            HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var source = await db.Rooms.AsNoTracking()
                .Include(r => r.SubRooms)
                .FirstOrDefaultAsync(r => r.Id == roomId, ct);
            if (source is null) return Results.NotFound();
            if (!source.CloningAllowed && source.CreatorAccountId != caller.Id)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var clone = new Room
            {
                Name = (await http.ReadStringAsync("name")) ?? $"{source.Name} copy",
                Description = source.Description,
                ImageName = source.ImageName,
                CreatorAccountId = caller.Id,
                Accessibility = source.Accessibility,
                MaxPlayers = source.MaxPlayers,
                MinLevel = source.MinLevel,
                SupportsJuniors = source.SupportsJuniors,
                SupportsScreens = source.SupportsScreens,
                SupportsTeleportVR = source.SupportsTeleportVR,
                SupportsWalkVR = source.SupportsWalkVR,
                TagsCsv = source.TagsCsv,
                AutoTagsCsv = source.AutoTagsCsv,
                WarningMask = source.WarningMask,
                CustomWarning = source.CustomWarning,
                DataBlob = source.DataBlob,
                DataBlobHash = source.DataBlobHash,
                UnityAssetId = source.UnityAssetId,
                CreatedAt = DateTime.UtcNow,
            };

            foreach (var sub in source.SubRooms)
            {
                clone.SubRooms.Add(new SubRoom
                {
                    Name = sub.Name,
                    Description = sub.Description,
                    ImageName = sub.ImageName,
                    MaxPlayers = sub.MaxPlayers,
                    Accessibility = sub.Accessibility,
                    IsSpawnPoint = sub.IsSpawnPoint,
                    DataBlob = sub.DataBlob,
                    DataBlobHash = sub.DataBlobHash,
                });
            }

            db.Rooms.Add(clone);
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(clone.Id, db, ct);
        });
    }

    private static void MapSubRooms(WebApplication app)
    {
        MapRoom(app, "POST rooms/{roomId:long}/subrooms", async (
            HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db,
            RecEmuOptionsAccessor options, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            db.SubRooms.Add(new SubRoom
            {
                RoomId = roomId,
                Name = (await http.ReadStringAsync("name")) ?? "New Subroom",
                MaxPlayers = options.Value.DefaultRoomCapacity,
                Accessibility = Enums.Accessibility.Public,
            });
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "DELETE rooms/{roomId:long}/subrooms/{subRoomId:long}", async (
            long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (subRoom is not null) db.SubRooms.Remove(subRoom);
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/subrooms/{subRoomId:long}/name", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (subRoom is null) return Results.NotFound();

            subRoom.Name = (await http.ReadStringAsync("name")) ?? subRoom.Name;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // Int enum, under the key "accessibility" — the older handler bound a JSON "Value".
        MapRoom(app, "PUT rooms/{roomId:long}/subrooms/{subRoomId:long}/accessibility", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (subRoom is null) return Results.NotFound();

            subRoom.Accessibility = await http.ReadIntAsync("accessibility") ?? subRoom.Accessibility;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/subrooms/{subRoomId:long}/maxplayers", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db,
            RecEmuOptionsAccessor options, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (subRoom is null) return Results.NotFound();

            subRoom.MaxPlayers = Math.Clamp(
                await http.ReadIntAsync("maxPlayers") ?? options.Value.DefaultRoomCapacity,
                1, options.Value.MaxRoomCapacity);
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/subrooms/{subRoomId:long}/modify", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db,
            RecEmuOptionsAccessor options, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (subRoom is null) return Results.NotFound();

            subRoom.Name = (await http.ReadStringAsync("name")) ?? subRoom.Name;
            subRoom.Accessibility = await http.ReadIntAsync("accessibility") ?? subRoom.Accessibility;
            subRoom.MaxPlayers = Math.Clamp(
                await http.ReadIntAsync("maxPlayers") ?? options.Value.DefaultRoomCapacity,
                1, options.Value.MaxRoomCapacity);

            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "POST rooms/{roomId:long}/subrooms/{subRoomId:long}/move", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (subRoom is null) return Results.NotFound();

            var targetRoomId = await http.ReadLongAsync("newRoomId") ?? roomId;
            if (targetRoomId == roomId) return await DetailsAsync(roomId, db, ct);

            // Moving between rooms is a transfer, not a reorder: the destination room has to
            // exist and the caller has to be allowed to put a scene in it.
            var target = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == targetRoomId, ct);
            if (target is null) return Results.NotFound();
            if (!await Rooms.CanManageAsync(db, targetRoomId, caller.Id, Enums.RoomRole.CoOwner, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            subRoom.RoomId = targetRoomId;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(targetRoomId, db, ct);
        });

        MapRoom(app, "POST rooms/{roomId:long}/subrooms/{subRoomId:long}/clone", async (
            long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var source = await db.SubRooms.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (source is null) return Results.NotFound();

            db.SubRooms.Add(new SubRoom
            {
                RoomId = roomId,
                Name = source.Name + " copy",
                Description = source.Description,
                ImageName = source.ImageName,
                MaxPlayers = source.MaxPlayers,
                Accessibility = source.Accessibility,
                DataBlob = source.DataBlob,
                DataBlobHash = source.DataBlobHash,
            });
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        // The only rooms route with a real JSON body, and the only one that wraps its answer in a
        // legacy success envelope. Both are load-bearing.
        MapRoom(app, "POST rooms/{roomId:long}/subrooms/{subRoomId:long}/data", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (subRoom is null) return Results.NotFound();

            var payload = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject;
            if (payload is null) return Bare.Ok();

            var unityAssetId = payload["UnityAssetId"]?.ToString();
            var roomData = payload["RoomData"] as JsonObject;
            var subRoomData = payload["SubRoomData"] as JsonObject;
            _ = roomData;

            if (!string.IsNullOrWhiteSpace(unityAssetId)) room.UnityAssetId = unityAssetId!;

            var filename = subRoomData?["Filename"]?.ToString();
            if (!string.IsNullOrWhiteSpace(filename))
            {
                var save = new SubRoomDataSave
                {
                    RoomId = roomId,
                    SubRoomId = subRoomId,
                    UnityAssetId = unityAssetId ?? string.Empty,
                    // The editor uploads the scene through the storage service; what the room gets
                    // is the blob name, and the CDN serves the bytes from there.
                    DataBlob = filename!,
                    DataBlobHash = subRoomData?["Hash"]?.ToString() ?? string.Empty,
                    Description = subRoomData?["Description"]?.ToString() ?? string.Empty,
                    SavedByAccountId = caller.Id,
                };
                db.SubRoomDataSaves.Add(save);
                subRoom.DataBlob = save.DataBlob;
                subRoom.DataBlobHash = save.DataBlobHash;
            }

            await db.SaveChangesAsync(ct);

            // This route nests the room inside the legacy success envelope, so it needs the
            // details *object*, not an IResult — hence the separate lookup rather than DetailsAsync.
            var details = await DetailsOrNullAsync(roomId, db, ct) ?? new Dictionary<string, object?>();

            return Results.Json(Obj.Create(
                ("success", true),
                ("value", Obj.Create(
                    ("Room", details),
                    ("SubRoomDataSaveId", filename is null ? 0 : await LatestSaveIdAsync(db, subRoomId, ct))))), Json.Options);
        });

        MapRoom(app, "POST rooms/{roomId:long}/subrooms/{subRoomId:long}/publish_save", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var saveId = await http.ReadLongAsync("subRoomDataSaveId") ?? 0;
            var save = await db.SubRoomDataSaves.AsNoTracking().FirstOrDefaultAsync(s => s.Id == saveId && s.SubRoomId == subRoomId, ct);
            if (save is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstAsync(s => s.Id == subRoomId, ct);
            subRoom.DataBlob = save.DataBlob;
            subRoom.DataBlobHash = save.DataBlobHash;
            room.UnityAssetId = save.UnityAssetId;
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });

        MapRoom(app, "GET rooms/{roomId:long}/subrooms/{subRoomId:long}/saves", async (
            HttpContext http, long roomId, long subRoomId, RecEmuDb db, CancellationToken ct) =>
        {
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 20, 1, 100);

            var saves = await db.SubRoomDataSaves.AsNoTracking()
                .Where(s => s.RoomId == roomId && s.SubRoomId == subRoomId)
                .OrderByDescending(s => s.Id)
                .ToListAsync(ct);

            return Results.Json(Obj.Paged(saves.Skip(skip).Take(take).Select(Wire.SubRoomSaveRow), saves.Count), Json.Options);
        });

        MapRoom(app, "PUT rooms/{roomId:long}/subrooms/{subRoomId:long}/permissions", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, RecEmuDb db,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var room = await LoadManageableAsync(roomId, caller.Id, db, ct);
            if (room is null) return Results.NotFound();

            var subRoom = await db.SubRooms.FirstOrDefaultAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (subRoom is null) return Results.NotFound();

            // The client sends a serialized permission list; keeping it opaque is deliberate,
            // because the Value variant's scalar type is not recoverable from the client.
            subRoom.PermissionsJson = (await http.ReadStringAsync("permissions")) ?? "[]";
            await db.SaveChangesAsync(ct);
            return await DetailsAsync(roomId, db, ct);
        });
    }

    private static async Task<long> LatestSaveIdAsync(RecEmuDb db, long subRoomId, CancellationToken ct)
        => await db.SubRoomDataSaves.AsNoTracking()
            .Where(s => s.SubRoomId == subRoomId)
            .OrderByDescending(s => s.Id)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(ct) ?? 0;

    private static async Task<Room?> LoadManageableAsync(long roomId, int callerId, RecEmuDb db, CancellationToken ct)
    {
        if (!await Rooms.CanManageAsync(db, roomId, callerId, Enums.RoomRole.Moderator, ct)) return null;
        return await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
    }

    private static async Task AssignRoleAsync(RecEmuDb db, long roomId, int accountId, int role, int changedBy, CancellationToken ct)
    {
        var row = await db.RoomRoles.FirstOrDefaultAsync(r => r.RoomId == roomId && r.AccountId == accountId, ct);
        if (row is null)
        {
            db.RoomRoles.Add(new RoomRoleAssignment
            {
                RoomId = roomId,
                AccountId = accountId,
                Role = role,
                LastChangedByAccountId = changedBy,
            });
        }
        else
        {
            row.Role = role;
            row.LastChangedByAccountId = changedBy;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Tells clients watching this room that it changed. Routed on RoomUpdate, one of the six ids
    /// the client registers by name rather than by number.
    /// </summary>
    private static async Task AnnounceRoomAsync(NotificationDispatcher notifications, Room room, CancellationToken ct)
        => await notifications.BroadcastAsync(NotificationId.RoomUpdate,
            Obj.Create(("RoomId", room.Id), ("Name", room.Name)), ct);
}
