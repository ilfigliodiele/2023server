using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

// Collides with System.Threading.Thread under implicit usings; these files mean the chat entity.
using Thread = RecEmu.Server.Data.Thread;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Threads and chat.
///
/// Rec Room's chat is message-based rather than socket-based: messages are posted over HTTP and
/// fan out through the SignalR push channel, not a chat socket. That distinction matters because it
/// means a message has to be persisted before it is pushed — otherwise a player who reconnects sees
/// a gap where their own message was, and the room looks lossy.
///
/// Threads come in three shapes and share one table: a room's chat, a direct-message group, and a
/// club thread. <c>Type</c> is what keeps them apart, and it is also the routing key for the push
/// audience: a room thread pushes to the room's occupants, everything else pushes to members.
/// </summary>
public static class ChatEndpoints
{
    private const int MaxMessageLength = 500;

    public static void Map(WebApplication app)
    {
        MapThreads(app);
        MapMessages(app);
        MapRoomChat(app);
    }

    private static void MapThreads(WebApplication app)
    {
        app.MapGet("/threads", async (HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Json(new List<object?>(), Json.Options);

            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 25, 1, 100);

            var ids = await db.ThreadMembers.AsNoTracking()
                .Where(m => m.AccountId == caller.Id)
                .Select(m => m.ThreadId)
                .ToListAsync(ct);

            var threads = await db.Threads.AsNoTracking()
                .Where(t => ids.Contains(t.Id) && !t.Archived)
                .OrderByDescending(t => t.Id)
                .ToListAsync(ct);

            return Results.Json(Obj.Paged(threads.Skip(skip).Take(take).Select(Wire.ThreadRow), threads.Count), Json.Options);
        });

        // One thread per pair. Creating a second one for the same pair leaves both in the DM list,
        // and the client has no way to merge them, so the lookup has to happen before the insert.
        app.MapPost("/threads/directmessage/{accountId:int}", async (
            int accountId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (accountId == caller.Id) return Results.BadRequest();
            if (!await db.Players.AnyAsync(p => p.Id == accountId && !p.IsDeleted, ct)) return Results.NotFound();

            var pair = new[] { caller.Id, accountId }.Order().ToArray();

            var existing = await FindDirectThreadAsync(db, pair[0], pair[1], ct);
            if (existing is not null) return Results.Json(Wire.ThreadRow(existing), Json.Options);

            var thread = new Thread
            {
                Type = "dm",
                CreatorAccountId = caller.Id,
                Name = "Direct Message",
            };
            db.Threads.Add(thread);
            await db.SaveChangesAsync(ct);

            db.ThreadMembers.AddRange(
                new ThreadMember { ThreadId = thread.Id, AccountId = pair[0] },
                new ThreadMember { ThreadId = thread.Id, AccountId = pair[1] });
            await db.SaveChangesAsync(ct);

            return Results.Json(Wire.ThreadRow(thread), Json.Options);
        });

        app.MapGet("/threads/{threadId:long}", async (
            long threadId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await IsMemberAsync(db, threadId, caller.Id, ct)) return Results.NotFound();

            var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, ct);
            return thread is null ? Results.NotFound() : Results.Json(Wire.ThreadRow(thread), Json.Options);
        });

        app.MapGet("/threads/{threadId:long}/members", async (
            long threadId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await IsMemberAsync(db, threadId, caller.Id, ct)) return Results.NotFound();

            var ids = await db.ThreadMembers.AsNoTracking()
                .Where(m => m.ThreadId == threadId)
                .Select(m => m.AccountId)
                .ToListAsync(ct);

            var players = await db.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
            return Results.Json(players.Select(Wire.Account).ToList(), Json.Options);
        });

        app.MapPost("/threads/{threadId:long}/members/{accountId:int}", async (
            long threadId, int accountId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await IsMemberAsync(db, threadId, caller.Id, ct)) return Bare.Ok();

            var member = await db.ThreadMembers
                .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.AccountId == accountId, ct);
            if (member is null)
                db.ThreadMembers.Add(new ThreadMember { ThreadId = threadId, AccountId = accountId });
            else
                member.Muted = false;

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapDelete("/threads/{threadId:long}/members/{accountId:int}", async (
            long threadId, int accountId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var member = await db.ThreadMembers
                .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.AccountId == accountId, ct);
            if (member is null) return Bare.Ok();

            var isSelf = accountId == caller.Id;
            if (!isSelf && !await IsMemberAsync(db, threadId, caller.Id, ct)) return Bare.Ok();

            db.ThreadMembers.Remove(member);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapMessages(WebApplication app)
    {
        app.MapGet("/threads/{threadId:long}/messages", async (
            HttpContext http, long threadId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Json(new List<object?>(), Json.Options);
            if (!await IsMemberAsync(db, threadId, caller.Id, ct)) return Results.Json(new List<object?>(), Json.Options);

            // Newest-first with a skip, because the client loads the newest page and pages backwards.
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 50, 1, 200);

            var messages = await db.ChatMessages.AsNoTracking()
                .Where(m => m.ThreadId == threadId)
                .OrderByDescending(m => m.Id)
                .ToListAsync(ct);

            var senders = await db.Players.AsNoTracking()
                .Where(p => messages.Select(m => m.SenderAccountId).Contains(p.Id))
                .ToListAsync(ct);
            var byId = senders.ToDictionary(p => p.Id);

            var page = messages.Skip(skip).Take(take).Reverse();

            return Results.Json(page
                .Select(m => Wire.ChatMessageRow(m, byId.GetValueOrDefault(m.SenderAccountId)))
                .ToList(), Json.Options);
        });

        // Persist first, then push. A push that is not backed by a stored message disappears
        // entirely for anyone who reconnects, and a room whose chat loses messages under packet loss
        // is much harder to diagnose than one that loses them consistently.
        app.MapPost("/threads/{threadId:long}/messages", async (
            HttpContext http, long threadId, CurrentPlayerAccessor current, RecEmuDb db,
            PresenceService presence, NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await IsMemberAsync(db, threadId, caller.Id, ct)) return Results.Forbid();

            var member = await db.ThreadMembers
                .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.AccountId == caller.Id, ct);
            if (member?.Muted == true) return Results.Forbid();

            var text = (await http.ReadStringAsync("message", "text"))?.Trim() ?? string.Empty;
            if (text.Length == 0) return Results.BadRequest();
            if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];

            var message = new ChatMessage
            {
                ThreadId = threadId,
                SenderAccountId = caller.Id,
                Message = text,
                SentTime = DateTime.UtcNow,
            };

            db.ChatMessages.Add(message);
            await db.SaveChangesAsync(ct);

            await FanOutAsync(db, presence, notifications, threadId, caller, message, ct);

            return Results.Json(Wire.ChatMessageRow(message, caller), Json.Options);
        });

        app.MapMethods("/threads/{threadId:long}/messages/{messageId:long}", ["PUT", "DELETE"], async (
            long threadId, long messageId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var message = await db.ChatMessages
                .FirstOrDefaultAsync(m => m.Id == messageId && m.ThreadId == threadId, ct);
            if (message is null) return Bare.Ok();

            var owns = message.SenderAccountId == caller.Id;
            if (!owns && !await IsClubModeratorAsync(db, threadId, caller.Id, ct)) return Bare.Ok();

            // Soft delete. Removing the row loses the sender's place in the transcript, and the
            // client re-paginates by id, so a gap there renumbers everything after it.
            message.DeletedAt = DateTime.UtcNow;
            message.Message = string.Empty;
            await db.SaveChangesAsync(ct);

            await notifications.SendToPlayersAsync(
                await MemberIdsAsync(db, threadId, ct), NotificationId.ChatMessageReceived,
                Obj.Create(
                    ("ThreadId", threadId),
                    ("MessageId", message.Id),
                    ("Deleted", true)), ct);

            return Bare.Ok();
        });

        app.MapPut("/threads/{threadId:long}/members/{accountId:int}/mute", async (
            long threadId, int accountId, HttpContext http, CurrentPlayerAccessor current, RecEmuDb db,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await IsMemberAsync(db, threadId, caller.Id, ct)) return Bare.Ok();

            var member = await db.ThreadMembers
                .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.AccountId == accountId, ct);
            if (member is null) return Bare.Ok();

            member.Muted = await http.ReadBoolAsync("muted") ?? member.Muted;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapRoomChat(WebApplication app)
    {
        // The in-room chat bar posts to a room-scoped route rather than a thread id, so the room's
        // thread is resolved — and created — on the way through.
        app.MapGet("/rooms/{roomId:long}/messages", async (
            HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Json(new List<object?>(), Json.Options);

            var threadId = await EnsureRoomThreadAsync(db, roomId, ct);
            if (threadId is null) return Results.Json(new List<object?>(), Json.Options);

            return await ReadMessagesAsync(http, threadId.Value, caller.Id, db, ct);
        });

        app.MapPost("/rooms/{roomId:long}/messages", async (
            HttpContext http, long roomId, CurrentPlayerAccessor current, RecEmuDb db,
            PresenceService presence, NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await db.Rooms.AnyAsync(r => r.Id == roomId, ct)) return Results.NotFound();
            if (await Rooms.IsBannedAsync(db, roomId, caller.Id, ct)) return Results.Forbid();

            var threadId = await EnsureRoomThreadAsync(db, roomId, ct);
            if (threadId is null) return Results.NotFound();

            var text = (await http.ReadStringAsync("message", "text"))?.Trim() ?? string.Empty;
            if (text.Length == 0) return Results.BadRequest();
            if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];

            var message = new ChatMessage
            {
                ThreadId = threadId.Value,
                SenderAccountId = caller.Id,
                Message = text,
                SentTime = DateTime.UtcNow,
            };

            db.ChatMessages.Add(message);
            await db.SaveChangesAsync(ct);

            // Everyone in the room, not thread members: a room's chat is transient and membership
            // would mean a per-join thread row, which is write amplification on the join path.
            var instanceId = presence.RoomInstanceOf(caller.Id);
            var occupants = instanceId is null ? new List<int>() : presence.OccupantsOf(instanceId.Value);

            var payload = Wire.ChatMessageRow(message, caller);
            payload["RoomId"] = roomId;

            await notifications.SendToPlayersAsync(
                occupants.Concat(new[] { caller.Id }), NotificationId.ChatMessageReceived, payload, ct);

            return Results.Json(payload, Json.Options);
        });
    }

    private static async Task<IResult> ReadMessagesAsync(
        HttpContext http, long threadId, int accountId, RecEmuDb db, CancellationToken ct)
    {
        var skip = await http.ReadIntAsync("skip") ?? 0;
        var take = Math.Clamp(await http.ReadIntAsync("take") ?? 50, 1, 200);

        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ThreadId == threadId)
            .OrderByDescending(m => m.Id)
            .ToListAsync(ct);

        var senders = await db.Players.AsNoTracking()
            .Where(p => messages.Select(m => m.SenderAccountId).Contains(p.Id))
            .ToListAsync(ct);
        var byId = senders.ToDictionary(p => p.Id);

        return Results.Json(messages.Skip(skip).Take(take).Reverse()
            .Select(m => Wire.ChatMessageRow(m, byId.GetValueOrDefault(m.SenderAccountId)))
            .ToList(), Json.Options);
    }

    private static async Task FanOutAsync(
        RecEmuDb db, PresenceService presence, NotificationDispatcher notifications, long threadId,
        Player sender, ChatMessage message, CancellationToken ct)
    {
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread is null) return;

        var payload = Wire.ChatMessageRow(message, sender);
        payload["RoomId"] = thread.RoomId ?? 0;
        payload["ClubId"] = thread.ClubId ?? 0;

        if (thread.RoomId is { } roomId)
        {
            var instanceId = presence.RoomInstanceOf(sender.Id);
            var occupants = instanceId is null ? new List<int>() : presence.OccupantsOf(instanceId.Value);

            await notifications.SendToPlayersAsync(
                occupants.Concat(new[] { sender.Id }), NotificationId.ChatMessageReceived, payload, ct);
            _ = roomId;
            return;
        }

        await notifications.SendToPlayersAsync(
            await MemberIdsAsync(db, threadId, ct), NotificationId.ChatMessageReceived, payload, ct);
    }

    /// <summary>
    /// The single chat thread belonging to a room, created on first use.
    ///
    /// One thread per room rather than one per occupancy session: the transcript has to outlive any
    /// individual session, and a per-session thread means the room's chat history resets every time
    /// the last player leaves.
    /// </summary>
    private static async Task<long?> EnsureRoomThreadAsync(RecEmuDb db, long roomId, CancellationToken ct)
    {
        var existing = await db.Threads.AsNoTracking()
            .Where(t => t.RoomId == roomId && t.Type == "room")
            .Select(t => (long?)t.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null) return null;

        var thread = new Thread
        {
            Type = "room",
            RoomId = roomId,
            Name = $"{room.Name} Chat",
            CreatorAccountId = room.CreatorAccountId,
        };

        db.Threads.Add(thread);
        await db.SaveChangesAsync(ct);
        return thread.Id;
    }

    private static async Task<Thread?> FindDirectThreadAsync(RecEmuDb db, int a, int b, CancellationToken ct)
    {
        var candidates = await db.Threads.AsNoTracking()
            .Where(t => t.Type == "dm")
            .OrderByDescending(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (candidates.Count == 0) return null;

        // Both members must be present, otherwise a thread could match on one side alone and a
        // group thread would be handed back as a direct message.
        var matches = await db.ThreadMembers.AsNoTracking()
            .Where(m => candidates.Contains(m.ThreadId) && (m.AccountId == a || m.AccountId == b))
            .GroupBy(m => m.ThreadId)
            .Select(g => new { ThreadId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        foreach (var match in matches.Where(m => m.Count == 2).OrderByDescending(m => m.ThreadId))
        {
            var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == match.ThreadId, ct);
            if (thread is not null && !thread.Archived) return thread;
        }

        return null;
    }

    private static Task<bool> IsMemberAsync(RecEmuDb db, long threadId, int accountId, CancellationToken ct)
        => db.ThreadMembers.AsNoTracking().AnyAsync(m => m.ThreadId == threadId && m.AccountId == accountId, ct);

    private static async Task<List<int>> MemberIdsAsync(RecEmuDb db, long threadId, CancellationToken ct)
        => await db.ThreadMembers.AsNoTracking()
            .Where(m => m.ThreadId == threadId)
            .Select(m => m.AccountId)
            .ToListAsync(ct);

    private static async Task<bool> IsClubModeratorAsync(RecEmuDb db, long threadId, int accountId, CancellationToken ct)
    {
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread?.ClubId is not { } clubId) return false;

        var club = await db.Clubs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clubId, ct);
        if (club is null) return false;
        if (club.CreatorAccountId == accountId) return true;

        var role = await db.ClubMembers.AsNoTracking()
            .Where(m => m.ClubId == clubId && m.AccountId == accountId)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);

        return role >= Enums.RoomRole.Moderator;
    }
}