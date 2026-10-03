using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;

namespace RecEmu.Server.Endpoints;

/// <summary>Shared friend-graph queries, so presence pushes and the social routes agree.</summary>
public static class Social
{
    /// <summary>
    /// Accounts that should receive this player's presence updates.
    ///
    /// A block wins over friendship in both directions, and a pending request is not a friend.
    /// With GlobalFriends on, every other account is a friend by configuration and no rows are
    /// written at all — flipping the setting off therefore takes effect instantly with nothing to
    /// migrate, which is what makes it safe to expose as an operator toggle.
    /// </summary>
    public static async Task<List<int>> FriendIdsAsync(
        RecEmuDb db, int accountId, RecEmuOptionsAccessor? options, CancellationToken cancellationToken)
    {
        var stored = await db.Relationships.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.IsFriend && !r.IsBlocked && !r.Pending)
            .Select(r => r.TargetAccountId)
            .ToListAsync(cancellationToken);

        if (options?.Value.GlobalFriends != true) return stored;

        var everyone = await db.Players.AsNoTracking()
            .Where(p => p.Id != accountId && !p.IsDeleted)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var blocked = await db.Relationships.AsNoTracking()
            .Where(r => (r.AccountId == accountId && r.IsBlocked) || (r.TargetAccountId == accountId && r.IsBlocked))
            .Select(r => r.AccountId == accountId ? r.TargetAccountId : r.AccountId)
            .ToListAsync(cancellationToken);

        var result = everyone.Where(id => !blocked.Contains(id)).ToHashSet();
        foreach (var id in stored) result.Add(id);
        return result.ToList();
    }

    public static Task<List<int>> FriendIdsAsync(RecEmuDb db, int accountId, CancellationToken cancellationToken)
        => FriendIdsAsync(db, accountId, null, cancellationToken);

    /// <summary>Makes the friendship symmetric. Rec Room's friend list is mutual by definition.</summary>
    public static async Task BeFriendsAsync(RecEmuDb db, int a, int b, CancellationToken cancellationToken)
    {
        foreach (var (from, to) in new[] { (a, b), (b, a) })
        {
            var row = await db.Relationships.FirstOrDefaultAsync(r => r.AccountId == from && r.TargetAccountId == to, cancellationToken);
            if (row is null)
            {
                db.Relationships.Add(new Relationship
                {
                    AccountId = from,
                    TargetAccountId = to,
                    IsFriend = true,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                row.IsFriend = true;
                row.Pending = false;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
