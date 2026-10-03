using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Moderation: reports, warnings, kicks, and the console's view of both.
///
/// The push id used for a ban matters more than it looks. The protocol has a dedicated ban event,
/// and the client registers no handler for it — sending it means the banned player is never told and
/// keeps believing they are in the room until the next heartbeat fails. So bans ride on the kick
/// event with an IsBan flag, which the client does handle.
/// </summary>
public static class ModerationEndpoints
{
    public static void Map(WebApplication app)
    {
        MapReports(app);
        MapWarnings(app);
        MapKicks(app);
        MapConsole(app);
    }

    private static void MapReports(WebApplication app)
    {
        app.MapMethods("/moderation/report", ["POST", "PUT"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var reportedPlayer = await http.ReadIntAsync("reportedPlayerId", "ReportedPlayerId", "accountId");
            var reportedRoom = await http.ReadLongAsync("reportedRoomId", "ReportedRoomId", "roomId");
            var category = await http.ReadIntAsync("category", "Category", "ReportCategory");
            var details = await http.ReadStringAsync("details", "Details", "message") ?? string.Empty;
            var imageName = await http.ReadStringAsync("imageName", "reportedImageName");

            if (reportedPlayer is null && reportedRoom is null) return BadRequest();

            db.Reports.Add(new Report
            {
                ReporterAccountId = caller.Id,
                Category = category ?? 0,
                Details = details,
                ReportedPlayerId = reportedPlayer,
                ReportedRoomId = reportedRoom,
                ReportedImageName = imageName,
            });

            await db.SaveChangesAsync(ct);
            return Bare.Ok();

            static IResult BadRequest() => Results.StatusCode(StatusCodes.Status400BadRequest);
        });

        app.MapMethods("/api/moderation/v1/report", ["POST", "PUT"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var reportedPlayer = await http.ReadIntAsync("reportedPlayerId", "ReportedPlayerId", "accountId");
            var reportedRoom = await http.ReadLongAsync("reportedRoomId", "ReportedRoomId", "roomId");

            db.Reports.Add(new Report
            {
                ReporterAccountId = caller.Id,
                Category = await http.ReadIntAsync("category", "Category") ?? 0,
                Details = await http.ReadStringAsync("details", "Details") ?? string.Empty,
                ReportedPlayerId = reportedPlayer,
                ReportedRoomId = reportedRoom,
            });

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/moderation/reports", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!caller.IsModerator && !caller.IsAdmin) return Results.Json(new List<object?>(), Json.Options);

            var pending = await db.Reports.AsNoTracking()
                .Where(r => !r.Resolved)
                .OrderBy(r => r.Id)
                .Take(200)
                .ToListAsync(ct);

            return Results.Json(pending.Select(Wire.ReportRow).ToList(), Json.Options);
        });

        app.MapPut("/moderation/reports/{reportId:long}/resolve", async (
            long reportId, HttpContext http, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!caller.IsModerator && !caller.IsAdmin) return Results.Forbid();

            var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
            if (report is null) return Bare.Ok();

            report.Resolved = true;

            // A report can carry an action: resolving it while naming a player bans them, which is
            // how the console's one-click "report → ban" flow is expected to behave.
            var banPlayerId = await http.ReadIntAsync("banPlayerId");
            if (banPlayerId is not null)
            {
                var player = await db.Players.FirstOrDefaultAsync(p => p.Id == banPlayerId, ct);
                if (player is not null)
                {
                    player.IsBanned = true;
                    await db.SaveChangesAsync(ct);
                    await notifications.BanAsync(new[] { player.Id }, 0, "account suspended", ct);
                }
            }

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapWarnings(WebApplication app)
    {
        // The client shows this before any content-warning flow, so it has to be present on the
        // warning-gated screens. An absent route there reads as "no warning data" and the flow is
        // skipped entirely.
        app.MapGet("/moderation/warnings/me", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var warning = await db.PlayerWarnings.AsNoTracking()
                .Where(w => w.PlayerId == caller.Id && w.AcknowledgedAt == null)
                .OrderByDescending(w => w.Id)
                .FirstOrDefaultAsync(ct);

            if (warning is null) return Bare.Ok();

            return Results.Json(Obj.Create(
                ("PlayerWarningId", warning.Id),
                ("AccountId", caller.Id),
                ("WarningMask", warning.WarningMask),
                ("CustomWarning", warning.CustomWarning),
                ("CreatedAt", Wire.Iso(warning.CreatedAt))), Json.Options);
        });

        app.MapMethods("/moderation/warnings/{warningId:long}/acknowledge", ["PUT", "POST"], async (
            long warningId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var warning = await db.PlayerWarnings
                .FirstOrDefaultAsync(w => w.Id == warningId && w.PlayerId == caller.Id, ct);
            if (warning is null) return Bare.Ok();

            warning.AcknowledgedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapPut("/moderation/warnings/me/acknowledge", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var warnings = await db.PlayerWarnings
                .Where(w => w.PlayerId == caller.Id && w.AcknowledgedAt == null)
                .ToListAsync(ct);

            foreach (var warning in warnings) warning.AcknowledgedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapPost("/moderation/warnings", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!caller.IsModerator && !caller.IsAdmin) return Results.Forbid();

            var playerId = await http.ReadIntAsync("playerId", "accountId");
            if (playerId is null) return Results.BadRequest();

            db.PlayerWarnings.Add(new PlayerWarning
            {
                PlayerId = playerId.Value,
                WarningMask = await http.ReadIntAsync("warningMask") ?? Enums.WarningMask.None,
                CustomWarning = await http.ReadStringAsync("customWarning") ?? string.Empty,
            });

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapKicks(WebApplication app)
    {
        app.MapPost("/moderation/kick", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, PresenceService presence, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var targetId = await http.ReadIntAsync("playerId", "accountId", "reportedPlayerId");
            if (targetId is null) return Results.BadRequest();

            var roomId = await http.ReadLongAsync("roomId") ?? 0;
            var reason = await http.ReadStringAsync("reason") ?? string.Empty;

            await notifications.SendToPlayerAsync(targetId.Value, NotificationId.ModerationKick, Obj.Create(
                ("AccountId", targetId.Value),
                ("RoomId", roomId),
                ("Reason", reason),
                ("IsBan", false)), ct);

            // Leaving presence and occupancy together is what stops the kicked player being counted
            // in the instance and keeps the reaper from retiring a room they still occupy.
            if (presence.RoomInstanceOf(targetId.Value) is { } instanceId)
            {
                var occupied = presence.OccupantsOf(instanceId);
                if (occupied.Remove(targetId.Value) && occupied.Count == 0) presence.SetRoom(targetId.Value, null);
            }

            return Bare.Ok();
        });

        app.MapPost("/moderation/unkick", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var targetId = await http.ReadIntAsync("playerId", "accountId");
            if (targetId is null) return Results.BadRequest();

            await notifications.SendToPlayerAsync(targetId.Value, NotificationId.ModerationUnkick, Obj.Create(
                ("AccountId", targetId.Value),
                ("RoomId", await http.ReadLongAsync("roomId") ?? 0)), ct);

            return Bare.Ok();
        });

        // Room comments are the other moderation surface: they are user-visible text pinned in a
        // room, so a report here has to be able to name a comment, not just an account.
        app.MapGet("/rooms/{roomId:long}/roomcomments", async (
            HttpContext http, long roomId, RecEmuDb db, CancellationToken ct) =>
        {
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 50, 1, 200);

            var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, ct);
            if (room is null || room.DisableRoomComments) return Results.Json(new List<object?>(), Json.Options);

            var comments = await db.RoomComments.AsNoTracking()
                .Where(c => c.RoomId == roomId)
                .OrderByDescending(c => c.Id)
                .ToListAsync(ct);

            var authors = await db.Players.AsNoTracking()
                .Where(p => comments.Select(c => c.CreatorAccountId).Contains(p.Id))
                .ToListAsync(ct);
            var byId = authors.ToDictionary(p => p.Id);

            return Results.Json(Obj.Paged(
                comments.Skip(skip).Take(take).Select(c => Wire.CommentRow(c, byId.GetValueOrDefault(c.CreatorAccountId))),
                comments.Count), Json.Options);
        });

        app.MapPost("/rooms/{roomId:long}/roomcomments", async (
            HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, ct);
            if (room is null || room.DisableRoomComments) return Results.Forbid();
            if (await Rooms.IsBannedAsync(db, roomId, caller.Id, ct)) return Results.Forbid();

            var comment = new RoomComment
            {
                RoomId = roomId,
                SubRoomId = await http.ReadLongAsync("subRoomId"),
                Message = (await http.ReadStringAsync("message", "text"))?.Trim() ?? string.Empty,
                CreatorAccountId = caller.Id,
                Color = await http.ReadIntAsync("color") ?? 0,
                ImageName = await http.ReadStringAsync("imageName") ?? "none",
            };

            if (comment.Message.Length == 0) return Results.BadRequest();
            if (comment.Message.Length > 200) comment.Message = comment.Message[..200];

            db.RoomComments.Add(comment);
            await db.SaveChangesAsync(ct);

            await notifications.BroadcastAsync(NotificationId.CommunityBoardUpdate,
                Wire.CommentRow(comment, caller), ct);

            return Results.Json(Wire.CommentRow(comment, caller), Json.Options);
        });

        app.MapDelete("/rooms/{roomId:long}/roomcomments/{commentId:long}", async (
            long roomId, long commentId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var comment = await db.RoomComments
                .FirstOrDefaultAsync(c => c.Id == commentId && c.RoomId == roomId, ct);
            if (comment is null) return Bare.Ok();

            var owns = comment.CreatorAccountId == caller.Id;
            if (!owns && !await Rooms.CanManageAsync(db, roomId, caller.Id, Enums.RoomRole.Moderator, ct))
            {
                return Bare.Ok();
            }

            db.RoomComments.Remove(comment);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapConsole(WebApplication app)
    {
        // The in-client moderation console. Read-only summaries, so an operator can see the state of
        // the server without a separate admin tool; writes go through the room and account routes.
        app.MapGet("/moderation/console/summary", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!caller.IsModerator && !caller.IsAdmin) return Results.Forbid();

            return Results.Json(Obj.Create(
                ("OpenReports", await db.Reports.CountAsync(r => !r.Resolved, ct)),
                ("TotalReports", await db.Reports.CountAsync(ct)),
                ("TotalRooms", await db.Rooms.CountAsync(r => r.State == 0, ct)),
                ("BannedAccounts", await db.Players.CountAsync(p => p.IsBanned, ct)),
                ("TotalAccounts", await db.Players.CountAsync(p => !p.IsDeleted, ct))), Json.Options);
        });

        app.MapGet("/moderation/console/accounts", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!caller.IsModerator && !caller.IsAdmin) return Results.Forbid();

            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 50, 1, 200);

            var players = await db.Players.AsNoTracking()
                .Where(p => !p.IsDeleted)
                .OrderBy(p => p.Id)
                .ToListAsync(ct);

            return Results.Json(Obj.Paged(
                players.Skip(skip).Take(take).Select(Wire.Account), players.Count), Json.Options);
        });

        app.MapPut("/moderation/accounts/{accountId:int}/ban", async (
            HttpContext http, int accountId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!caller.IsAdmin) return Results.Forbid();

            var player = await db.Players.FirstOrDefaultAsync(p => p.Id == accountId, ct);
            if (player is null) return Results.NotFound();

            var banning = !(await http.ReadBoolAsync("unban") ?? false);
            player.IsBanned = banning;
            await db.SaveChangesAsync(ct);

            if (banning) await notifications.BanAsync(new[] { accountId }, 0, "account suspended", ct);

            return Bare.Ok();
        });
    }
}