using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Room ownership and permission checks, in one place.
///
/// Every room mutation funnels through <see cref="CanManageAsync"/> rather than comparing
/// <c>CreatorAccountId</c> at each call site. The comparison alone is not enough: Rec Room rooms are
/// collaborative, and the client assigns Host/Moderator/CoOwner roles that are expected to keep
/// working after the creator hands the room over. Checking only the creator column therefore locks
/// out everyone the owner promoted, and the client surfaces that as "you do not have permission to
/// edit" on a room the player can visibly open.
/// </summary>
public static class Rooms
{
    /// <summary>Roles at or above this level may mutate room settings.</summary>
    public const int EditorRole = Enums.RoomRole.Moderator;

    /// <summary>
    /// Whether an account may perform an action that requires at least <paramref name="requiredRole"/>.
    ///
    /// The ladder is linear — Host and CoOwner outrank Moderator, None outranks nothing — so the
    /// comparison is a numeric one. Admin and server ownership bypass it entirely, because an
    /// operator being locked out of a room they are moderating is never the intended outcome.
    /// </summary>
    public static async Task<bool> CanManageAsync(
        RecEmuDb db, long roomId, int accountId, int requiredRole, CancellationToken cancellationToken)
    {
        var room = await db.Rooms.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roomId, cancellationToken);
        if (room is null) return false;

        if (room.CreatorAccountId == accountId) return true;

        var player = await db.Players.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == accountId && !p.IsDeleted && !p.IsBanned, cancellationToken);
        if (player is null) return false;
        if (player.IsAdmin) return true;

        if (requiredRole <= Enums.RoomRole.None) return true;

        var assigned = await db.RoomRoles.AsNoTracking()
            .Where(r => r.RoomId == roomId && r.AccountId == accountId)
            .Select(r => new { r.Role, r.InvitedRole })
            .FirstOrDefaultAsync(cancellationToken);

        if (assigned is null) return false;

        // An invite that has not been accepted does not grant access, so only Role counts here.
        return assigned.Role >= requiredRole;
    }

    /// <summary>Whether the account owns the room outright. Used for destructive routes.</summary>
    public static async Task<bool> IsOwnerAsync(RecEmuDb db, long roomId, int accountId, CancellationToken cancellationToken)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, cancellationToken);
        if (room is null) return false;
        if (room.CreatorAccountId == accountId) return true;

        var player = await db.Players.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == accountId && !p.IsDeleted, cancellationToken);
        return player?.IsAdmin == true;
    }

    /// <summary>Whether the account is banned from the room at all, honouring the join ban mask.</summary>
    public static async Task<bool> IsBannedAsync(
        RecEmuDb db, long roomId, int accountId, CancellationToken cancellationToken)
    {
        var ban = await db.RoomBans.AsNoTracking()
            .Where(b => b.RoomId == roomId && b.BannedPlayerId == accountId)
            .Select(b => new { b.BanMask })
            .FirstOrDefaultAsync(cancellationToken);

        return ban is not null && (ban.BanMask & Enums.BanMask.Join) != 0;
    }
}