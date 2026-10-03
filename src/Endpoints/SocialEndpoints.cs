using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// The social graph: friends, friend requests, blocks and favourites.
///
/// Two things here are load-bearing and easy to get wrong. Friendship is symmetric — the client
/// renders one list and a one-directional row means the friend appears in one player's list and not
/// the other's, which reads as "the other player added me" forever. And a block has to beat
/// friendship in both directions: a blocked pair that still counts as friends keeps exchanging
/// presence pushes and chat, which is the opposite of what blocking is for.
/// </summary>
public static class SocialEndpoints
{
    public static void Map(WebApplication app)
    {
        MapFriends(app);
        MapRequests(app);
        MapBlocks(app);
        MapFavourites(app);
    }

    private static void MapFriends(WebApplication app)
    {
        app.MapMethods("/account/me/friends", ["GET", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, RecEmuOptionsAccessor options,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            if (http.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var ids = await ReadAccountIdsAsync(http);
                foreach (var id in ids)
                {
                    if (id == caller.Id) continue;
                    if (!await db.Players.AnyAsync(p => p.Id == id && !p.IsDeleted, ct)) continue;
                    await Social.BeFriendsAsync(db, caller.Id, id, ct);
                }

                foreach (var id in ids)
                    await notifications.SendToPlayerAsync(id, NotificationId.AccountUpdate,
                        Obj.Create(("AccountId", caller.Id), ("IsFriend", true)), ct);

                return Bare.Ok();
            }

            // With GlobalFriends on the friend list is every account, which is what makes a small
            // private server usable without anyone having to send requests first.
            if (options.Value.GlobalFriends)
            {
                var everyone = await db.Players.AsNoTracking()
                    .Where(p => p.Id != caller.Id && !p.IsDeleted && !p.IsBanned)
                    .OrderBy(p => p.Id)
                    .ToListAsync(ct);
                return Results.Json(everyone.Select(p => Wire.RelationshipRow(p, true, false, false)).ToList(), Json.Options);
            }

            var friends = await FriendRowsAsync(db, caller.Id, ct);
            return Results.Json(friends, Json.Options);
        });

        // A bare boolean, not an object: the "are we friends" indicator reads a primitive.
        app.MapGet("/account/me/friends/{accountId:int}", async (
            int accountId, CurrentPlayerAccessor current, RecEmuDb db, RecEmuOptionsAccessor options,
            CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Bare.Bool(false);
            if (options.Value.GlobalFriends && caller.Id != accountId) return Bare.Bool(true);
            return Bare.Bool(await db.Relationships.AsNoTracking()
                .AnyAsync(r => r.AccountId == caller.Id && r.TargetAccountId == accountId &&
                               r.IsFriend && !r.IsBlocked && !r.Pending, ct));
        });

        app.MapDelete("/account/me/friends/{accountId:int}", async (
            int accountId, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var edges = await db.Relationships
                .Where(r => (r.AccountId == caller.Id && r.TargetAccountId == accountId) ||
                            (r.AccountId == accountId && r.TargetAccountId == caller.Id))
                .ToListAsync(ct);

            if (edges.Count > 0)
            {
                db.Relationships.RemoveRange(edges);
                await db.SaveChangesAsync(ct);
            }

            await notifications.SendToPlayerAsync(accountId, NotificationId.AccountUpdate,
                Obj.Create(("AccountId", caller.Id), ("IsFriend", false)), ct);
            return Bare.Ok();
        });

        app.MapGet("/account/{accountId:int}/friends", async (
            int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var rows = await FriendRowsAsync(db, accountId, ct);
            return Results.Json(rows, Json.Options);
        });

        app.MapGet("/account/{accountId:int}/friendrequests", async (
            int accountId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (caller.Id != accountId && !caller.IsAdmin) return Results.Json(new List<object?>(), Json.Options);

            var pending = await db.FriendRequests.AsNoTracking()
                .Where(r => r.ReceiverAccountId == accountId && !r.Accepted && !r.Declined)
                .ToListAsync(ct);
            var senders = await db.Players.AsNoTracking()
                .Where(p => pending.Select(r => r.SenderAccountId).Contains(p.Id))
                .ToListAsync(ct);
            var byId = senders.ToDictionary(p => p.Id);

            return Results.Json(pending
                .Where(r => byId.ContainsKey(r.SenderAccountId))
                .Select(r => Json.WithAliases(
                    Wire.RelationshipRow(byId[r.SenderAccountId], false, false, true),
                    ("FriendRequestId", r.Id),
                    ("Direction", "outgoing")))
                .ToList(), Json.Options);
        });
    }

    private static void MapRequests(WebApplication app)
    {
        app.MapMethods("/account/me/friendrequests", ["GET", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, RecEmuOptionsAccessor options,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            if (http.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var ids = await ReadAccountIdsAsync(http);
                foreach (var target in ids)
                {
                    if (target == caller.Id) continue;
                    if (!await db.Players.AnyAsync(p => p.Id == target && !p.IsDeleted, ct)) continue;
                    if (await db.Relationships.AsNoTracking()
                        .AnyAsync(r => (r.AccountId == caller.Id && r.TargetAccountId == target && r.IsBlocked) ||
                                       (r.AccountId == target && r.TargetAccountId == caller.Id && r.IsBlocked), ct))
                    {
                        continue;
                    }

                    // Existing friends do not get a pending request; re-sending one is a no-op
                    // rather than a duplicate row, because the client's friend screen sends the
                    // list it already has on every refresh.
                    var alreadyFriends = options.Value.GlobalFriends || await db.Relationships.AsNoTracking()
                        .AnyAsync(r => r.AccountId == caller.Id && r.TargetAccountId == target && r.IsFriend, ct);
                    if (alreadyFriends) continue;

                    var existing = await db.FriendRequests
                        .FirstOrDefaultAsync(r => r.SenderAccountId == caller.Id && r.ReceiverAccountId == target &&
                                                 !r.Accepted && !r.Declined, ct);
                    if (existing is null)
                    {
                        db.FriendRequests.Add(new FriendRequest
                        {
                            SenderAccountId = caller.Id,
                            ReceiverAccountId = target,
                        });
                    }
                    else
                    {
                        existing.Declined = false;
                        existing.CreatedAt = DateTime.UtcNow;
                    }
                }

                await db.SaveChangesAsync(ct);

                foreach (var target in ids)
                    await notifications.SendToPlayerAsync(target, NotificationId.AccountUpdate,
                        Obj.Create(("AccountId", caller.Id), ("Pending", true)), ct);

                return Bare.Ok();
            }

            var incoming = await db.FriendRequests.AsNoTracking()
                .Where(r => r.ReceiverAccountId == caller.Id && !r.Accepted && !r.Declined)
                .ToListAsync(ct);
            var senders = await db.Players.AsNoTracking()
                .Where(p => incoming.Select(r => r.SenderAccountId).Contains(p.Id))
                .ToListAsync(ct);
            var byId = senders.ToDictionary(p => p.Id);

            return Results.Json(incoming
                .Where(r => byId.ContainsKey(r.SenderAccountId))
                .Select(r => Json.WithAliases(
                    Wire.RelationshipRow(byId[r.SenderAccountId], false, false, true),
                    ("FriendRequestId", r.Id),
                    ("Direction", "incoming")))
                .ToList(), Json.Options);
        });

        app.MapMethods("/account/me/friendrequests/{requestId:long}", ["PUT", "POST", "DELETE"], async (
            HttpContext http, long requestId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var request = await db.FriendRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
            if (request is null) return Bare.Ok();

            var declining = http.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

            // Only the receiver decides. The sender "cancelling" is handled by DELETE with no id,
            // which is why an unowned request must not be accepted here.
            if (request.ReceiverAccountId == caller.Id)
            {
                if (declining) request.Declined = true;
                else
                {
                    request.Accepted = true;
                    request.Declined = false;
                    await Social.BeFriendsAsync(db, request.SenderAccountId, caller.Id, ct);
                }
            }
            else if (request.SenderAccountId == caller.Id && declining)
            {
                db.FriendRequests.Remove(request);
            }
            else
            {
                return Bare.Ok();
            }

            await db.SaveChangesAsync(ct);

            await notifications.SendToPlayerAsync(request.SenderAccountId, NotificationId.AccountUpdate,
                Obj.Create(
                    ("AccountId", caller.Id),
                    ("FriendRequestId", request.Id),
                    ("Accepted", request.Accepted),
                    ("Declined", request.Declined)), ct);

            return Bare.Ok();
        });

        app.MapDelete("/account/me/friendrequests", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var ids = await ReadAccountIdsAsync(http);
            if (ids.Count > 0)
            {
                var rows = await db.FriendRequests
                    .Where(r => r.SenderAccountId == caller.Id && ids.Contains(r.ReceiverAccountId) && !r.Accepted)
                    .ToListAsync(ct);
                if (rows.Count > 0)
                {
                    db.FriendRequests.RemoveRange(rows);
                    await db.SaveChangesAsync(ct);
                }
            }

            return Bare.Ok();
        });
    }

    private static void MapBlocks(WebApplication app)
    {
        app.MapGet("/account/me/blocked", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var blocked = await db.Relationships.AsNoTracking()
                .Where(r => r.AccountId == caller.Id && r.IsBlocked)
                .Select(r => r.TargetAccountId)
                .ToListAsync(ct);

            var players = await db.Players.AsNoTracking()
                .Where(p => blocked.Contains(p.Id))
                .ToListAsync(ct);

            return Results.Json(players
                .Select(p => Json.WithAliases(Wire.RelationshipRow(p, false, false, false), ("IsBlocked", true)))
                .ToList(), Json.Options);
        });

        app.MapMethods("/account/me/blocked/{accountId:int}", ["PUT", "POST", "DELETE"], async (
            HttpContext http, int accountId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var blocking = !http.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

            if (blocking && accountId == caller.Id) return Bare.Ok();

            var outbound = await db.Relationships
                .FirstOrDefaultAsync(r => r.AccountId == caller.Id && r.TargetAccountId == accountId, ct);
            var inbound = await db.Relationships
                .FirstOrDefaultAsync(r => r.AccountId == accountId && r.TargetAccountId == caller.Id, ct);

            void Apply(Relationship? row, bool value)
            {
                if (row is null)
                {
                    if (!value) return;
                    db.Relationships.Add(new Relationship
                    {
                        AccountId = caller.Id,
                        TargetAccountId = accountId,
                        IsBlocked = true,
                    });
                    return;
                }

                row.IsBlocked = value;
                if (value) row.IsFriend = false;
            }

            Apply(outbound, blocking);
            Apply(inbound, blocking);

            // Removing a block restores nothing: the friendship that the block cleared is gone, and
            // silently reinstating it would put the two back in each other's lists unasked.
            if (!blocking)
            {
                if (outbound is not null) outbound.Pending = false;
                if (inbound is not null) inbound.Pending = false;
            }

            await db.SaveChangesAsync(ct);

            await notifications.SendToPlayerAsync(accountId, NotificationId.AccountUpdate,
                Obj.Create(("AccountId", caller.Id), ("IsBlocked", blocking)), ct);

            return Bare.Ok();
        });

        app.MapGet("/account/{accountId:int}/blocked", async (int accountId, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            // Only the blocker may see their own block list; anyone else gets false rather than 403,
            // because the client's own list view treats a non-true answer as "not blocked".
            return Bare.Bool(caller.Id == accountId);
        });
    }

    private static void MapFavourites(WebApplication app)
    {
        app.MapMethods("/account/me/favorites", ["GET", "PUT", "POST", "DELETE"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var favouriting = !http.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

            var ids = await ReadAccountIdsAsync(http);
            if (favouriting)
            {
                foreach (var id in ids)
                {
                    if (id == caller.Id) continue;
                    var row = await db.Relationships
                        .FirstOrDefaultAsync(r => r.AccountId == caller.Id && r.TargetAccountId == id, ct);
                    if (row is null)
                    {
                        db.Relationships.Add(new Relationship
                        {
                            AccountId = caller.Id,
                            TargetAccountId = id,
                            IsFavorite = true,
                        });
                    }
                    else
                    {
                        row.IsFavorite = true;
                    }
                }

                await db.SaveChangesAsync(ct);
                return Bare.Ok();
            }

            var rows = await db.Relationships
                .Where(r => r.AccountId == caller.Id && r.IsFavorite &&
                            (ids.Count == 0 || ids.Contains(r.TargetAccountId)))
                .ToListAsync(ct);
            if (rows.Count > 0)
            {
                foreach (var row in rows) row.IsFavorite = false;
                await db.SaveChangesAsync(ct);
            }

            if (ids.Count == 0) return Bare.Ok();

            foreach (var id in ids)
                await notifications.SendToPlayerAsync(id, NotificationId.AccountUpdate,
                    Obj.Create(("AccountId", caller.Id), ("IsFavorite", false)), ct);

            return Bare.Ok();
        });

        app.MapGet("/account/me/favorites", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var ids = await db.Relationships.AsNoTracking()
                .Where(r => r.AccountId == caller.Id && r.IsFavorite)
                .Select(r => r.TargetAccountId)
                .ToListAsync(ct);

            var players = await db.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
            return Results.Json(players
                .Select(p => Wire.RelationshipRow(p, true, true, false))
                .ToList(), Json.Options);
        });
    }

    private static async Task<List<Dictionary<string, object?>>> FriendRowsAsync(RecEmuDb db, int accountId, CancellationToken ct)
    {
        var ids = await db.Relationships.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.IsFriend && !r.IsBlocked && !r.Pending)
            .Select(r => r.TargetAccountId)
            .ToListAsync(ct);

        var blocked = await db.Relationships.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.IsBlocked)
            .Select(r => r.TargetAccountId)
            .ToListAsync(ct);

        var players = await db.Players.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && !p.IsDeleted && !blocked.Contains(p.Id))
            .ToListAsync(ct);

        return players.Select(p => Wire.RelationshipRow(p, true, false, false)).ToList();
    }

    /// <summary>
    /// Reads a repeated id list. The client sends repeated <c>id=</c> fields on one route and a
    /// comma-joined <c>ids=</c> on another, so both spellings are accepted here rather than
    /// silently producing an empty target set on half the routes.
    /// </summary>
    private static async Task<List<int>> ReadAccountIdsAsync(HttpContext http)
    {
        var raw = (await http.ReadValuesAsync("id", "ids", "accountId", "friendId"))
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(v => int.TryParse(v, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        return raw;
    }
}