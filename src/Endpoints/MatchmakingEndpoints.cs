using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Matchmaking;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Matchmaking and presence — the routes that decide which Photon room a player lands in.
///
/// Everything here answers with the same envelope: <c>{"errorCode":…, "roomInstance":…}</c>. The
/// client reads errorCode first and shows "Matchmaking request failed (n)" on anything non-zero,
/// so a wrong code is immediately visible, while a malformed roomInstance fails later and much
/// less legibly. When there is no instance to give, the key is still emitted and carries an empty
/// object rather than null — some deserializers treat a null there as a hard failure.
/// </summary>
public static class MatchmakingEndpoints
{
    /// <summary>What a matchmake handler needs beyond its own route arguments.</summary>
    ///
    /// Registered as a scoped service rather than assembled per handler: the same four objects are
    /// needed by every matchmaking route, and threading them through each signature is what makes it
    /// easy for a route to end up with a different combination than intended.
    public sealed record Session(
        RecEmuDb Db,
        RoomInstanceService Instances,
        OccupancyTracker Occupancy,
        PresenceService Presence);

    public static void Map(WebApplication app)
    {
        MapMatchmake(app);
        MapPresence(app);
        MapRoomInstance(app);
    }

    private static void MapMatchmake(WebApplication app)
    {
        // matchmake/none is a teardown call: only the status matters and the body is ignored.
        app.MapPost("/matchmake/none", () => Results.Json(Wire.MatchmakingResponse(Enums.MatchError.Ok, null), Json.Options));

        app.MapPost("/matchmake/dorm", async (
            HttpContext http, CurrentPlayerAccessor current, Session session, CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);

            // The Dorm is where a fresh install lands. It has to exist or nothing can ever join, so it is
            // resolved through Seed.FindDormAsync, which tracks the room by id rather than by name.
            var dorm = await Seed.FindDormAsync(session.Db, ct);
            if (dorm is null) return Fail(Enums.MatchError.RoomDoesNotExist);

            var requested = await http.ReadLongAsync("SubRoomId");
            var subRoomId = requested ?? dorm.SubRooms.OrderBy(s => s.Id).Select(s => (long?)s.Id).FirstOrDefault();
            if (subRoomId is null) return Fail(Enums.MatchError.RoomDoesNotExist);

            var joinMode = await http.ReadIntAsync("JoinMode") ?? Enums.JoinMode.JoinShared;
            return await GoAsync(session, player.Id, dorm.Id, subRoomId.Value, joinMode, null, ct);
        });

        app.MapPost("/matchmake/room/{roomId:long}", async (
            HttpContext http, long roomId, CurrentPlayerAccessor current, Session session, CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);
            var joinMode = await http.ReadIntAsync("JoinMode") ?? Enums.JoinMode.JoinShared;
            return await GoAsync(session, player.Id, roomId, null, joinMode, null, ct);
        });

        app.MapPost("/matchmake/room/{roomId:long}/{subRoomId:long}", async (
            HttpContext http, long roomId, long subRoomId, CurrentPlayerAccessor current, Session session,
            CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);
            var joinMode = await http.ReadIntAsync("JoinMode") ?? Enums.JoinMode.JoinShared;

            var owned = await session.Db.SubRooms.AsNoTracking().AnyAsync(s => s.Id == subRoomId && s.RoomId == roomId, ct);
            if (!owned) return Fail(Enums.MatchError.RoomDoesNotExist);

            return await GoAsync(session, player.Id, roomId, subRoomId, joinMode, null, ct);
        });

        app.MapPost("/matchmake/code/{roomId:long}/{code}", async (
            HttpContext http, long roomId, string code, CurrentPlayerAccessor current, Session session,
            CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);
            var (instance, error) = await session.Instances.JoinByCodeAsync(roomId, code, ct);
            if (error != Enums.MatchError.Ok) return Fail(error);

            // A code can point at a room with several scenes; the client may narrow it further.
            var requestedSubRoom = await http.ReadLongAsync("SubRoomId");
            if (requestedSubRoom is not null) instance!.SubRoomId = requestedSubRoom.Value;

            return await OkAsync(session, player.Id, instance!, ct);
        });

        app.MapPost("/matchmake/instance/{instanceId:long}", async (
            long instanceId, CurrentPlayerAccessor current, Session session, CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);
            var instance = await session.Instances.FindAsync(instanceId, ct);
            return instance is null ? Fail(Enums.MatchError.RoomInstanceDoesNotExist) : await OkAsync(session, player.Id, instance, ct);
        });

        // A follow-join must land in the same Photon room as the target, so the target's instance
        // is mirrored verbatim rather than matched into a shared one.
        app.MapPost("/matchmake/player/{playerId:int}", async (
            int playerId, CurrentPlayerAccessor current, Session session, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var (instance, error) = await session.Instances.JoinByPlayerAsync(playerId, ct);
            return error != Enums.MatchError.Ok ? Fail(error) : await OkAsync(session, caller.Id, instance!, ct);
        });

        app.MapPost("/matchmake/invite/{inviteId:long}", async (
            long inviteId, CurrentPlayerAccessor current, Session session, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var request = await session.Db.FriendRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == inviteId && r.ReceiverAccountId == caller.Id, ct);
            if (request is null) return Fail(Enums.MatchError.Failed);

            var (instance, error) = await session.Instances.JoinByPlayerAsync(request.SenderAccountId, ct);
            return error != Enums.MatchError.Ok ? Fail(error) : await OkAsync(session, caller.Id, instance!, ct);
        });

        app.MapPost("/matchmake/chatinvite/{threadId:long}/{accountId:int}", async (
            long threadId, int accountId, CurrentPlayerAccessor current, Session session, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            _ = threadId;
            var (instance, error) = await session.Instances.JoinByPlayerAsync(accountId, ct);
            return error != Enums.MatchError.Ok ? Fail(error) : await OkAsync(session, caller.Id, instance!, ct);
        });

        app.MapPost("/matchmake/club/{clubId:long}", async (
            long clubId, CurrentPlayerAccessor current, Session session, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var club = await session.Db.Clubs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clubId, ct);
            if (club?.ClubhouseRoomId is null) return Fail(Enums.MatchError.RoomDoesNotExist);

            var subRoomId = await session.Db.SubRooms.AsNoTracking()
                .Where(s => s.RoomId == club.ClubhouseRoomId.Value)
                .OrderBy(s => s.Id)
                .Select(s => (long?)s.Id)
                .FirstOrDefaultAsync(ct);
            if (subRoomId is null) return Fail(Enums.MatchError.RoomDoesNotExist);

            return await GoAsync(session, caller.Id, club.ClubhouseRoomId.Value, subRoomId.Value,
                Enums.JoinMode.JoinShared, clubId.ToString(), ct);
        });

        app.MapPost("/matchmake/event/{eventId:long}", async (
            long eventId, CurrentPlayerAccessor current, Session session, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var roomId = await session.Db.Creations.AsNoTracking()
                .Where(c => c.Id == eventId && c.Type == "event")
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);
            return roomId == 0 ? Fail(Enums.MatchError.RoomDoesNotExist)
                               : await GoAsync(session, caller.Id, roomId, null, Enums.JoinMode.JoinShared, null, ct);
        });
    }

    private static IResult Fail(int errorCode)
        => Results.Json(Wire.MatchmakingResponse(errorCode, null), Json.Options);

    private static async Task<IResult> GoAsync(
        Session session, int playerId, long roomId, long? subRoomId, int joinMode, string? clubId,
        CancellationToken cancellationToken)
    {
        long target = subRoomId ?? await session.Db.SubRooms.AsNoTracking()
            .Where(s => s.RoomId == roomId)
            .OrderBy(s => s.Id)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        if (target == 0) return Fail(Enums.MatchError.RoomDoesNotExist);

        var (instance, error) = await session.Instances.AcquireAsync(roomId, target, joinMode, clubId, cancellationToken);
        return error != Enums.MatchError.Ok ? Fail(error) : await OkAsync(session, playerId, instance!, cancellationToken);
    }

    private static async Task<IResult> OkAsync(Session session, int playerId, RoomInstance instance, CancellationToken cancellationToken)
    {
        // Moving to a new instance means leaving the old one. Occupancy and presence have to move
        // together, or the reaper treats a busy room as empty and deletes it out from under players.
        var previous = session.Presence.RoomInstanceOf(playerId);
        if (previous != instance.Id)
        {
            if (previous is not null) session.Occupancy.Leave(previous.Value);
            session.Occupancy.Join(instance.Id);
            session.Presence.SetRoom(playerId, instance.Id);
        }

        var roomName = await session.Instances.RoomNameAsync(instance.RoomId, cancellationToken);
        return Results.Json(
            Wire.MatchmakingResponse(Enums.MatchError.Ok, session.Instances.ToWire(instance, roomName)),
            Json.Options);
    }

    private static void MapPresence(WebApplication app)
    {
        // 409 here is how the client reaches its "already logged in somewhere else" branch.
        app.MapPost("/player/login", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, PresenceService presence,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var loginLock = (await http.ReadStringAsync("LoginLock")) ?? string.Empty;
            var deviceClass = (await http.ReadStringAsync("DeviceClass")) ?? string.Empty;

            var tracked = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            if (!string.IsNullOrEmpty(tracked.LoginLock) && tracked.LoginLock != loginLock)
                return Results.StatusCode(StatusCodes.Status409Conflict);

            tracked.LoginLock = loginLock;
            await db.SaveChangesAsync(ct);
            presence.TryConnect(caller.Id, loginLock, deviceClass, out _);

            await ReassertPresenceAsync(caller.Id, presence, notifications, ct);
            return Bare.Ok();
        });

        app.MapPost("/player/exclusivelogin", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, PresenceService presence,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var loginLock = (await http.ReadStringAsync("LoginLock")) ?? string.Empty;
            var takeOver = await http.ReadBoolAsync("TakeOverExclusiveSession") ?? false;

            var tracked = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            if (!takeOver && !string.IsNullOrEmpty(tracked.LoginLock) && tracked.LoginLock != loginLock)
                return Results.StatusCode(StatusCodes.Status409Conflict);

            tracked.LoginLock = loginLock;
            await db.SaveChangesAsync(ct);
            presence.TryConnect(caller.Id, loginLock, string.Empty, out _);
            return Bare.Ok();
        });

        app.MapPost("/player/logout", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, PresenceService presence,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var loginLock = (await http.ReadStringAsync("LoginLock")) ?? string.Empty;

            var tracked = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            if (tracked.LoginLock == loginLock) tracked.LoginLock = null;
            tracked.LastSeenTime = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            presence.Disconnect(caller.Id, loginLock);
            await PushOfflineAsync(caller.Id, presence, notifications, db, ct);
            return Bare.Ok();
        });

        // Parsed and compared against local state by the client, so appVersion must be the
        // supported build's string — the wrong value tags every player [VERSION MISMATCH].
        app.MapPost("/player/heartbeat", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, PresenceService presence,
            RecEmuOptionsAccessor options, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            presence.Heartbeat(caller.Id, (await http.ReadStringAsync("LoginLock")) ?? string.Empty);

            var instance = presence.RoomInstanceOf(caller.Id) is { } instanceId
                ? await db.RoomInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, ct)
                : null;

            if (instance is null)
            {
                // The instance the client thinks it is in is gone (restart, reaped). Clearing it
                // here stops the client showing a room that no longer exists all session.
                presence.SetRoom(caller.Id, null);
                return Results.Json(IdlePayload(caller, presence, options), Json.Options);
            }

            return Results.Json(presence.HeartbeatPayload(caller, instance.Id, instance.RoomId, instance.SubRoomId), Json.Options);
        });

        // Persisted rather than echoed: a value that is only reflected back resets on the next
        // boot, because the client re-reads it and only caches its own write for the session.
        app.MapPut("/player/statusvisibility", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, PresenceService presence,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var value = await http.ReadIntAsync("statusVisibility") ?? caller.StatusVisibility;
            presence.SetStatusVisibility(caller.Id, value);

            var tracked = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            tracked.StatusVisibility = value;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapPut("/player/vrmovementmode", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, PresenceService presence,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var value = await http.ReadIntAsync("vrMovementMode") ?? caller.VrMovementMode;
            presence.SetVrMovementMode(caller.Id, value);

            var tracked = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            tracked.VrMovementMode = value;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        // A bare boolean. The client uses a typed primitive reader, and an array or an object here
        // makes it log "Failed to get avoid juniors status".
        app.MapGet("/player/avoidjuniors", async (
            CurrentPlayerAccessor current, PresenceService presence, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            return Bare.Bool(presence.AvoidJuniorsOf(caller));
        });

        app.MapMethods("/player/avoidjuniors", ["PUT", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, PresenceService presence,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var value = await http.ReadBoolAsync("avoidJuniors") ?? caller.AvoidJuniors;
            presence.SetAvoidJuniors(caller.Id, value);

            var tracked = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            tracked.AvoidJuniors = value;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        // One form field per region keyed by the region name (us=42&eu=90) — not paired
        // region/ping arrays, which is why walking two known keys always comes back empty.
        app.MapMethods("/player/photonregionpings", ["PUT", "POST", "GET"], async (
            HttpContext http, CurrentPlayerAccessor current, PresenceService presence, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var pings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync();
                foreach (var (key, value) in form)
                {
                    if (key.Equals("region", StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.Equals("ping", StringComparison.OrdinalIgnoreCase)) continue;
                    if (int.TryParse(value.ToString(), out var ping)) pings[key] = Math.Clamp(ping, 0, 2000);
                }
            }

            var regions = await http.ReadValuesAsync("region");
            var raw = await http.ReadValuesAsync("ping");
            for (var i = 0; i < Math.Min(regions.Count, raw.Count); i++)
                if (int.TryParse(raw[i], out var ping)) pings[regions[i]] = Math.Clamp(ping, 0, 2000);

            presence.RecordRegionPings(caller.Id, pings);
            return Bare.Ok();
        });

        // 2023 sends PlayerId and RoomInstanceId; binding only the older otherPlayerId key leaves
        // the id null, so the remote disconnect is never recorded.
        app.MapPost("/player/notifydisconnect", async (
            HttpContext http, PresenceService presence, CancellationToken ct) =>
        {
            var playerId = await http.ReadIntAsync("PlayerId", "playerId", "otherPlayerId");
            if (playerId is not null) presence.SetOnline(playerId.Value, false);
            return Bare.Ok();
        });

        app.MapGet("/matchmaking/v1/presence/{playerId:int}", async (
            int playerId, RecEmuDb db, PresenceService presence, CancellationToken ct) =>
        {
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId && !p.IsDeleted, ct);
            if (player is null) return Results.NotFound();

            var instanceId = presence.RoomInstanceOf(playerId);
            long roomId = 0, subRoomId = 0;
            if (instanceId is not null)
            {
                var instance = await db.RoomInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, ct);
                if (instance is not null) { roomId = instance.RoomId; subRoomId = instance.SubRoomId; }
            }

            return Results.Json(presence.PublicPayload(player, instanceId, roomId, subRoomId), Json.Options);
        });
    }

    private static void MapRoomInstance(WebApplication app)
    {
        app.MapGet("/room/{roomId:long}/instances", async (
            long roomId, RoomInstanceService instances, OccupancyTracker occupancy, CancellationToken ct) =>
        {
            var live = await instances.ListForRoomAsync(roomId, ct);
            var rows = live.Select(i =>
            {
                var summary = Wire.InstanceSummary(i, Array.Empty<int>());
                summary["PlayerCount"] = occupancy.CountIn(i.Id);
                return summary;
            });
            return Results.Json(rows.ToList(), Json.Options);
        });

        // PUT, not POST: POST-only makes this 405 and the host cannot close the instance.
        app.MapMethods("/roominstance/{instanceId:long}/markprivate", ["PUT", "POST"], async (
            long instanceId, RoomInstanceService instances, CancellationToken ct) =>
        {
            await instances.MarkPrivateAsync(instanceId, ct);
            return Bare.Ok();
        });

        app.MapPut("/roominstance/{instanceId:long}/inprogress", async (
            HttpContext http, long instanceId, RoomInstanceService instances, CancellationToken ct) =>
        {
            await instances.SetInProgressAsync(instanceId, await http.ReadBoolAsync("inProgress") ?? false, ct);
            return Bare.Ok();
        });

        app.MapPost("/roominstance/{instanceId:long}/reportjoinresult", async (
            HttpContext http, long instanceId, RoomInstanceService instances, CancellationToken ct) =>
        {
            await instances.ReportJoinResultAsync(instanceId, await http.ReadIntAsync("result") ?? 0, ct);
            return Bare.Ok();
        });

        // The accepted code comes back as a bare JSON string. Ignoring the submitted code and
        // returning a generated one instead is why custom codes never stuck.
        app.MapMethods("/roominstance/{instanceId:long}/roomCode", ["PUT", "POST", "GET"], async (
            HttpContext http, long instanceId, RoomInstanceService instances, CancellationToken ct) =>
        {
            var code = await instances.SetRoomCodeAsync(
                instanceId,
                await http.ReadStringAsync("roomCode"),
                await http.ReadBoolAsync("forceChange") ?? false,
                ct);
            return Bare.Text(code);
        });

        app.MapGet("/rooms/requiring/{mode}", async (string mode, RecEmuDb db, CancellationToken ct) =>
        {
            var accessibility = mode.ToLowerInvariant() switch
            {
                "private" => Enums.Accessibility.InviteOnly,
                "hidden" => Enums.Accessibility.Hidden,
                _ => Enums.Accessibility.FriendsOfFriends,
            };

            var rooms = await db.Rooms.AsNoTracking()
                .Where(r => r.Accessibility == accessibility && r.State == 0)
                .OrderByDescending(r => r.CreatedAt)
                .Take(100)
                .ToListAsync(ct);

            return Results.Json(rooms.Select(Wire.RoomSummary).ToList(), Json.Options);
        });
    }

    private static Dictionary<string, object?> IdlePayload(Player player, PresenceService presence, RecEmuOptionsAccessor options)
        => Obj.Create(
            ("playerId", player.Id),
            ("statusVisibility", presence.StatusVisibilityOf(player)),
            ("deviceClass", string.Empty),
            ("vrMovementMode", presence.VrMovementModeOf(player)),
            ("roomInstanceId", 0),
            ("roomId", 0),
            ("subRoomId", 0),
            ("isOnline", true),
            ("appVersion", options.Value.AppVersion));

    private static async Task ReassertPresenceAsync(
        int playerId, PresenceService presence, NotificationDispatcher notifications,
        CancellationToken cancellationToken)
    {
        // A SignalR reconnect restores the socket but not the client's presence cache, so without
        // this the player stays greyed out for the rest of the session.
        if (presence.RoomInstanceOf(playerId) is null) return;
        await notifications.SendToPlayerAsync(playerId, NotificationId.PresenceUpdate,
            Obj.Create(("playerId", playerId), ("isOnline", true)), cancellationToken);
    }

    private static async Task PushOfflineAsync(
        int playerId, PresenceService presence, NotificationDispatcher notifications,
        RecEmuDb db, CancellationToken cancellationToken)
    {
        presence.SetOnline(playerId, false);
        var friends = await Social.FriendIdsAsync(db, playerId, cancellationToken);
        if (friends.Count == 0) return;

        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId, cancellationToken);
        if (player is null) return;

        await notifications.SendToPlayersAsync(friends, NotificationId.PresenceUpdate,
            presence.PublicPayload(player, null, 0, 0), cancellationToken);
    }
}
