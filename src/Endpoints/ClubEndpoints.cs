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
/// Clubs: the 2023 client's grouping feature, plus the announcements its club pages read.
///
/// A club is mostly a container around a room. What the client actually cares about is
/// <c>ClubhouseRoomId</c> — the club tab opens a club by matchmaking into that room — so the room
/// side of club creation is handled by creating a room here rather than expecting the caller to
/// have one already, which is what left clubs permanently joinable-but-unopenable before.
/// </summary>
public static class ClubEndpoints
{
    public static void Map(WebApplication app)
    {
        MapBrowse(app);
        MapMembership(app);
        MapModeration(app);
        MapAnnouncements(app);
    }

    private static void MapBrowse(WebApplication app)
    {
        app.MapGet("/clubs", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 25, 1, 100);

            var query = db.Clubs.AsNoTracking().Where(c => c.State == 0);

            var category = http.Request.Query["category"].ToString();
            if (!string.IsNullOrWhiteSpace(category)) query = query.Where(c => c.Category == category);

            var clubs = await query
                .OrderByDescending(c => c.MemberCount).ThenBy(c => c.Id)
                .ToListAsync(ct);

            return Results.Json(Obj.Paged(clubs.Skip(skip).Take(take).Select(Wire.Club), clubs.Count), Json.Options);
        });

        app.MapGet("/clubs/search", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var term = (await http.ReadStringAsync("query", "name"))?.Trim() ?? string.Empty;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 25, 1, 100);

            var clubs = await db.Clubs.AsNoTracking()
                .Where(c => c.State == 0 && (term.Length == 0 || c.Name.Contains(term)))
                .OrderByDescending(c => c.MemberCount)
                .Take(take)
                .ToListAsync(ct);

            return Results.Json(clubs.Select(Wire.Club).ToList(), Json.Options);
        });

        app.MapGet("/clubs/{clubId:long}", async (long clubId, RecEmuDb db, CancellationToken ct) =>
        {
            var club = await db.Clubs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clubId && c.State == 0, ct);
            return club is null ? Results.NotFound() : Results.Json(Wire.Club(club), Json.Options);
        });

        app.MapGet("/clubs/me", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var ids = await db.ClubMembers.AsNoTracking()
                .Where(m => m.AccountId == caller.Id)
                .Select(m => m.ClubId)
                .ToListAsync(ct);

            var clubs = await db.Clubs.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
            return Results.Json(clubs.Select(Wire.Club).ToList(), Json.Options);
        });

        // account/{id}/clubs is a *list*, unlike /clubs/me which the client reads as a single club.
        app.MapGet("/account/{accountId:int}/clubs", async (int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var ids = await db.ClubMembers.AsNoTracking()
                .Where(m => m.AccountId == accountId)
                .Select(m => m.ClubId)
                .ToListAsync(ct);

            var clubs = await db.Clubs.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
            return Results.Json(clubs.Select(Wire.Club).ToList(), Json.Options);
        });

        app.MapGet("/account/{accountId:int}/clubmemberships", async (int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var memberships = await db.ClubMembers.AsNoTracking()
                .Where(m => m.AccountId == accountId)
                .ToListAsync(ct);

            var clubs = await db.Clubs.AsNoTracking()
                .Where(c => memberships.Select(m => m.ClubId).Contains(c.Id))
                .ToListAsync(ct);

            return Results.Json(memberships
                .Where(m => clubs.Any(c => c.Id == m.ClubId))
                .Select(m => Wire.ClubMemberRow(m, clubs.First(c => c.Id == m.ClubId)))
                .ToList(), Json.Options);
        });

        app.MapGet("/clubs/categories", () => Results.Json(Obj.Create(
            ("Categories", new List<object?>
            {
                Obj.Create(("Name", "recroomofficial"), ("DisplayName", "Official")),
                Obj.Create(("Name", "community"), ("DisplayName", "Community")),
                Obj.Create(("Name", "usercreated"), ("DisplayName", "User Created")),
            })), Json.Options));
    }

    private static void MapMembership(WebApplication app)
    {
        app.MapGet("/clubs/{clubId:long}/members", async (
            long clubId, HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 50, 1, 200);

            var club = await db.Clubs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clubId, ct);
            if (club is null) return Results.NotFound();

            var members = await db.ClubMembers.AsNoTracking()
                .Where(m => m.ClubId == clubId)
                .OrderBy(m => m.JoinedAt)
                .ToListAsync(ct);

            return Results.Json(Obj.Paged(
                members.Skip(skip).Take(take).Select(m => Wire.ClubMemberRow(m, club)), members.Count), Json.Options);
        });

        app.MapGet("/clubs/{clubId:long}/members/{accountId:int}", async (
            long clubId, int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var member = await db.ClubMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ClubId == clubId && m.AccountId == accountId, ct);
            var club = await db.Clubs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clubId, ct);
            if (member is null || club is null) return Results.NotFound();
            return Results.Json(Wire.ClubMemberRow(member, club), Json.Options);
        });

        // The client opens a club's "Join" button with this route, and it distinguishes open /
        // ask / invite by the club's Joinability. An open club joins immediately; the other two
        // create a pending request, which approval then converts.
        app.MapMethods("/clubs/{clubId:long}/members/requesttojoin", ["POST", "PUT"], async (
            long clubId, CurrentPlayerAccessor current, RecEmuDb db, RecEmuOptionsAccessor options,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var club = await db.Clubs.FirstOrDefaultAsync(c => c.Id == clubId && c.State == 0, ct);
            if (club is null) return Results.NotFound();
            if (club.Joinability == Enums.ClubJoinability.InviteOnly) return Bare.Ok();
            if (await db.ClubMembers.AnyAsync(m => m.ClubId == clubId && m.AccountId == caller.Id, ct)) return Bare.Ok();

            var request = await db.Threads.FirstOrDefaultAsync(
                t => t.Type == "clubrequest" && t.ClubId == clubId && t.CreatorAccountId == caller.Id, ct);

            if (club.Joinability == Enums.ClubJoinability.Open)
            {
                await JoinAsync(db, club, caller.Id, ct);
                return Bare.Ok();
            }

            if (request is null)
            {
                db.Threads.Add(new Thread
                {
                    Type = "clubrequest",
                    ClubId = clubId,
                    CreatorAccountId = caller.Id,
                    Name = $"join request from {caller.Username}",
                });
                await db.SaveChangesAsync(ct);
            }

            return Bare.Ok();
        });

        // Bare boolean: the join button's state.
        app.MapGet("/clubs/{clubId:long}/members/{accountId:int}/requesttojoin", async (
            long clubId, int accountId, RecEmuDb db, CancellationToken ct) =>
            await db.ClubMembers.AsNoTracking().AnyAsync(m => m.ClubId == clubId && m.AccountId == accountId, ct)
                ? Bare.Bool(true)
                : Bare.Bool(false));

        app.MapMethods("/clubs/{clubId:long}/members/{accountId:int}/approve", ["PUT", "POST"], async (
            long clubId, int accountId, CurrentPlayerAccessor current, RecEmuDb db, NotificationDispatcher notifications,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await CanModerateAsync(db, clubId, caller.Id, ct)) return Bare.Ok();

            var club = await db.Clubs.FirstOrDefaultAsync(c => c.Id == clubId, ct);
            if (club is null) return Bare.Ok();

            await JoinAsync(db, club, accountId, ct);

            var request = await db.Threads.FirstOrDefaultAsync(
                t => t.Type == "clubrequest" && t.ClubId == clubId && t.CreatorAccountId == accountId && !t.Archived, ct);
            if (request is not null)
            {
                request.Archived = true;
                await db.SaveChangesAsync(ct);
            }

            await notifications.SendToPlayerAsync(accountId, NotificationId.AccountUpdate,
                Obj.Create(("ClubId", clubId), ("Joined", true)), ct);

            return Bare.Ok();
        });

        app.MapMethods("/clubs/{clubId:long}/members/{accountId:int}", ["DELETE", "PUT"], async (
            long clubId, int accountId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var leaving = accountId == caller.Id;
            if (!leaving && !await CanModerateAsync(db, clubId, caller.Id, ct)) return Bare.Ok();

            var member = await db.ClubMembers
                .FirstOrDefaultAsync(m => m.ClubId == clubId && m.AccountId == accountId, ct);

            if (member is not null)
            {
                db.ClubMembers.Remove(member);

                var club = await db.Clubs.FirstOrDefaultAsync(c => c.Id == clubId, ct);
                if (club is not null) club.MemberCount = Math.Max(0, club.MemberCount - 1);
                await db.SaveChangesAsync(ct);
            }

            await notifications.SendToPlayerAsync(accountId, NotificationId.AccountUpdate,
                Obj.Create(("ClubId", clubId), ("Joined", false)), ct);

            return Bare.Ok();
        });

        // Club creation makes the clubhouse room in the same call. Doing it here rather than
        // requiring a pre-existing room is what makes a freshly created club openable.
        app.MapMethods("/clubs", ["POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, RecEmuOptionsAccessor options,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var name = ((await http.ReadStringAsync("name")) ?? string.Empty).Trim();
            if (name.Length == 0) name = $"{caller.Username}'s Club";

            var room = new Room
            {
                Name = name,
                Description = await http.ReadStringAsync("description") ?? string.Empty,
                ImageName = await http.ReadStringAsync("mainImageName") ?? "none",
                CreatorAccountId = caller.Id,
                MaxPlayers = options.Value.DefaultRoomCapacity,
                Accessibility = Enums.Accessibility.FriendsOfFriends,
            };
            room.SubRooms.Add(new SubRoom
            {
                Name = "Clubhouse",
                MaxPlayers = options.Value.DefaultRoomCapacity,
                Accessibility = Enums.Accessibility.Public,
                IsSpawnPoint = true,
            });

            var club = new Club
            {
                Name = name,
                Description = await http.ReadStringAsync("description") ?? string.Empty,
                MainImageName = room.ImageName,
                CreatorAccountId = caller.Id,
                Category = await http.ReadStringAsync("category") ?? "community",
                Joinability = await http.ReadIntAsync("joinability") ?? Enums.ClubJoinability.Open,
                Visibility = await http.ReadIntAsync("visibility") ?? 0,
                ClubhouseRoomId = 0,
            };

            db.Rooms.Add(room);
            db.Clubs.Add(club);
            await db.SaveChangesAsync(ct);

            club.ClubhouseRoomId = room.Id;
            await JoinAsync(db, club, caller.Id, ct);
            await db.SaveChangesAsync(ct);

            return Results.Json(Wire.Club(club), Json.Options);
        });

        app.MapPut("/clubs/{clubId:long}/modify", async (
            HttpContext http, long clubId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await CanModerateAsync(db, clubId, caller.Id, ct)) return Results.NotFound();

            var club = await db.Clubs.FirstOrDefaultAsync(c => c.Id == clubId, ct);
            if (club is null) return Results.NotFound();

            if (await http.HasKeyAsync("name")) club.Name = (await http.ReadStringAsync("name")) ?? club.Name;
            if (await http.HasKeyAsync("description")) club.Description = await http.ReadStringAsync("description") ?? club.Description;
            if (await http.HasKeyAsync("mainImageName")) club.MainImageName = await http.ReadStringAsync("mainImageName") ?? club.MainImageName;
            if (await http.HasKeyAsync("joinability")) club.Joinability = await http.ReadIntAsync("joinability") ?? club.Joinability;
            if (await http.HasKeyAsync("visibility")) club.Visibility = await http.ReadIntAsync("visibility") ?? club.Visibility;
            if (await http.HasKeyAsync("category")) club.Category = await http.ReadStringAsync("category") ?? club.Category;
            if (await http.HasKeyAsync("clubChatEnabled")) club.ClubChatEnabled = await http.ReadBoolAsync("clubChatEnabled") ?? club.ClubChatEnabled;
            if (await http.HasKeyAsync("allowJuniors")) club.AllowJuniors = await http.ReadBoolAsync("allowJuniors") ?? club.AllowJuniors;

            await db.SaveChangesAsync(ct);
            return Results.Json(Wire.Club(club), Json.Options);
        });
    }

    private static void MapModeration(WebApplication app)
    {
        app.MapMethods("/clubs/{clubId:long}/members/{accountId:int}/role", ["PUT", "POST"], async (
            HttpContext http, long clubId, int accountId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await CanModerateAsync(db, clubId, caller.Id, ct)) return Bare.Ok();

            var member = await db.ClubMembers
                .FirstOrDefaultAsync(m => m.ClubId == clubId && m.AccountId == accountId, ct);
            if (member is null) return Bare.Ok();

            member.Role = await http.ReadIntAsync("role") ?? member.Role;
            if (await http.HasKeyAsync("chatDisabled")) member.ChatDisabled = await http.ReadBoolAsync("chatDisabled") ?? member.ChatDisabled;
            if (await http.HasKeyAsync("notificationsMuted")) member.NotificationsMuted = await http.ReadBoolAsync("notificationsMuted") ?? member.NotificationsMuted;

            await db.SaveChangesAsync(ct);

            await notifications.SendToPlayerAsync(accountId, NotificationId.AccountUpdate,
                Obj.Create(("ClubId", clubId), ("Role", member.Role)), ct);

            return Bare.Ok();
        });
    }

    private static void MapAnnouncements(WebApplication app)
    {
        app.MapGet("/clubs/{clubId:long}/announcements", async (
            long clubId, HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 25, 1, 100);

            var rows = await db.Announcements.AsNoTracking()
                .Where(a => a.ClubId == clubId)
                .OrderByDescending(a => a.Pinned)
                .ThenByDescending(a => a.Id)
                .ToListAsync(ct);

            return Results.Json(Obj.Paged(rows.Skip(skip).Take(take).Select(Wire.AnnouncementRow), rows.Count), Json.Options);
        });

        app.MapPost("/clubs/{clubId:long}/announcements", async (
            HttpContext http, long clubId, CurrentPlayerAccessor current, RecEmuDb db,
            NotificationDispatcher notifications, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await CanModerateAsync(db, clubId, caller.Id, ct)) return Bare.Ok();

            var announcement = new Announcement
            {
                ClubId = clubId,
                Title = await http.ReadStringAsync("title") ?? "Announcement",
                Message = await http.ReadStringAsync("message") ?? string.Empty,
                ImageName = await http.ReadStringAsync("imageName") ?? "none",
                CreatorAccountId = caller.Id,
                Pinned = await http.ReadBoolAsync("pinned") ?? false,
            };

            db.Announcements.Add(announcement);
            await db.SaveChangesAsync(ct);

            var memberIds = await db.ClubMembers.AsNoTracking()
                .Where(m => m.ClubId == clubId)
                .Select(m => m.AccountId)
                .ToListAsync(ct);

            await notifications.SendToPlayersAsync(memberIds, NotificationId.CommunityBoardUpdate,
                Wire.AnnouncementRow(announcement), ct);

            return Results.Json(Wire.AnnouncementRow(announcement), Json.Options);
        });

        app.MapDelete("/clubs/{clubId:long}/announcements/{announcementId:long}", async (
            long clubId, long announcementId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            if (!await CanModerateAsync(db, clubId, caller.Id, ct)) return Bare.Ok();

            var announcement = await db.Announcements
                .FirstOrDefaultAsync(a => a.Id == announcementId && a.ClubId == clubId, ct);
            if (announcement is null) return Bare.Ok();

            db.Announcements.Remove(announcement);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static async Task JoinAsync(RecEmuDb db, Club club, int accountId, CancellationToken ct)
    {
        if (await db.ClubMembers.AnyAsync(m => m.ClubId == club.Id && m.AccountId == accountId, ct)) return;

        db.ClubMembers.Add(new ClubMember { ClubId = club.Id, AccountId = accountId });
        club.MemberCount = Math.Max(0, club.MemberCount + 1);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Creator or club officer. Club roles use the same ladder as room roles.</summary>
    private static async Task<bool> CanModerateAsync(RecEmuDb db, long clubId, int accountId, CancellationToken ct)
    {
        var club = await db.Clubs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clubId, ct);
        if (club is null) return false;
        if (club.CreatorAccountId == accountId) return true;

        var player = await db.Players.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == accountId && !p.IsDeleted && !p.IsBanned, ct);
        if (player is null) return false;
        if (player.IsAdmin) return true;

        var role = await db.ClubMembers.AsNoTracking()
            .Where(m => m.ClubId == clubId && m.AccountId == accountId)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);

        return role >= Enums.RoomRole.Moderator;
    }
}