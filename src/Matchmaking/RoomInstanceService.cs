using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Matchmaking;

/// <summary>
/// Owns live room instances — who is in what, and which Photon room backs it.
///
/// An instance here is the *server's* record of a session; the authoritative player list lives in
/// Photon. Rec Room's design splits the two: matchmaking hands the client a roomInstance containing
/// the Photon room name and region, and the client then does all its networking through Photon.
/// That is why this service never proxies game traffic — and why a Photon outage shows up as
/// "everyone can join the lobby but nobody appears".
/// </summary>
public sealed class RoomInstanceService(
    RecEmuDb db,
    RecEmuOptionsAccessor options,
    OccupancyTracker occupancy,
    PresenceService presence,
    ILogger<RoomInstanceService> logger)
{
    private readonly RecEmuOptions _options = options.Value;

    /// <summary>
    /// Finds a joinable instance of a subroom, or creates one.
    ///
    /// JoinMode decides: 0 joins any shared instance, 1 always creates a public one, 2 always
    /// creates a private one. Private instances are excluded from the shared pool, so a private
    /// instance created by one player is never handed to the next.
    /// </summary>
    public async Task<(RoomInstance? Instance, int ErrorCode)> AcquireAsync(
        long roomId, long subRoomId, int joinMode, string? clubId, CancellationToken cancellationToken)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, cancellationToken);
        if (room is null) return (null, Enums.MatchError.RoomDoesNotExist);

        var subRoom = await db.SubRooms.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subRoomId, cancellationToken);
        if (subRoom is null) return (null, Enums.MatchError.RoomDoesNotExist);

        var capacity = subRoom.MaxPlayers > 0 ? subRoom.MaxPlayers : _options.DefaultRoomCapacity;
        var privateByRoom = room.Accessibility is Enums.Accessibility.InviteOnly or Enums.Accessibility.Hidden;

        if (joinMode is not (Enums.JoinMode.NewPublic or Enums.JoinMode.NewPrivate) && !privateByRoom)
        {
            var candidates = await db.RoomInstances
                .Where(i => i.RoomId == roomId && i.SubRoomId == subRoomId && !i.IsPrivate && !i.IsFull && !i.IsInProgress)
                .OrderBy(i => i.CreatedAt)
                .ToListAsync(cancellationToken);

            // Occupancy lives in memory, so the fullness filter has to happen here rather than in
            // the query — a full instance still has a row.
            var joinable = candidates.FirstOrDefault(i => occupancy.CountIn(i.Id) < i.MaxCapacity)
                           ?? candidates.FirstOrDefault(i => occupancy.CountIn(i.Id) == 0);

            if (joinable is not null)
            {
                if (joinable.MaxCapacity <= 0) joinable.MaxCapacity = capacity;
                joinable.IsFull = false;
                await db.SaveChangesAsync(cancellationToken);
                return (joinable, Enums.MatchError.Ok);
            }
        }

        var instance = new RoomInstance
        {
            RoomId = roomId,
            SubRoomId = subRoomId,
            IsPrivate = joinMode == Enums.JoinMode.NewPrivate || privateByRoom,
            MaxCapacity = capacity,
            PhotonRegionId = _options.Photon.Region,
            ClubId = clubId,
            EncryptVoiceChat = room.EncryptVoiceChat,
            Location = Guid.NewGuid().ToString("N"),
            PhotonRoomId = Guid.NewGuid().ToString("N"),
            RoomCode = Random.Shared.Next(100000, 999999).ToString(),
        };

        db.RoomInstances.Add(instance);
        await db.SaveChangesAsync(cancellationToken);

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("instance {InstanceId} created for room {RoomId} subroom {SubRoomId} private {Private}",
                instance.Id, roomId, subRoomId, instance.IsPrivate);

        return (instance, Enums.MatchError.Ok);
    }

    public Task<RoomInstance?> FindAsync(long instanceId, CancellationToken cancellationToken)
        => db.RoomInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);

    public Task<List<RoomInstance>> ListForRoomAsync(long roomId, CancellationToken cancellationToken)
        => db.RoomInstances.AsNoTracking().Where(i => i.RoomId == roomId).OrderBy(i => i.CreatedAt).ToListAsync(cancellationToken);

    public async Task<(RoomInstance? Instance, int ErrorCode)> JoinByCodeAsync(
        long roomId, string? code, CancellationToken cancellationToken)
    {
        var normalized = (code ?? string.Empty).Trim();
        if (normalized.Length == 0) return (null, Enums.MatchError.RoomCodeIsInvalid);

        var instance = await db.RoomInstances
            .Where(i => i.RoomId == roomId && (i.CustomRoomCode == normalized || i.RoomCode == normalized))
            .OrderBy(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (instance is null) return (null, Enums.MatchError.RoomCodeIsInvalid);
        if (occupancy.CountIn(instance.Id) >= instance.MaxCapacity) return (null, Enums.MatchError.RoomIsFull);
        return (instance, Enums.MatchError.Ok);
    }

    public async Task<(RoomInstance? Instance, int ErrorCode)> JoinByPlayerAsync(
        int targetPlayerId, CancellationToken cancellationToken)
    {
        var instanceId = presence.RoomInstanceOf(targetPlayerId);
        if (instanceId is null) return (null, Enums.MatchError.RoomInstanceDoesNotExist);

        var instance = await db.RoomInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null) return (null, Enums.MatchError.RoomInstanceDoesNotExist);
        if (instance.IsPrivate) return (null, Enums.MatchError.RoomInstanceIsPrivate);
        return (instance, Enums.MatchError.Ok);
    }

    public async Task<string> SetRoomCodeAsync(long instanceId, string? customCode, bool forceChange, CancellationToken cancellationToken)
    {
        var instance = await db.RoomInstances.FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null) return string.Empty;

        if (!string.IsNullOrWhiteSpace(customCode))
        {
            var normalized = customCode!.Trim();
            var takenByOther = await db.RoomInstances
                .AnyAsync(i => i.Id != instanceId && i.CustomRoomCode == normalized, cancellationToken);

            // Without forceChange a colliding custom code is refused and the generated one stays.
            if (!takenByOther || forceChange) instance.CustomRoomCode = normalized;
        }

        await db.SaveChangesAsync(cancellationToken);
        return instance.CustomRoomCode ?? instance.RoomCode;
    }

    public async Task MarkPrivateAsync(long instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.RoomInstances.FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null) return;
        instance.IsPrivate = true;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetInProgressAsync(long instanceId, bool inProgress, CancellationToken cancellationToken)
    {
        var instance = await db.RoomInstances.FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null) return;
        instance.IsInProgress = inProgress;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReportJoinResultAsync(long instanceId, int result, CancellationToken cancellationToken)
    {
        // result 0 is a successful join. Anything else means the client could not get in, so the
        // instance is retired rather than left in the shared pool for the next player.
        if (result == 0) return;

        var instance = await db.RoomInstances.FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null) return;
        instance.IsFull = true;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Dictionary<string, object?> ToWire(RoomInstance instance, string roomName)
        => Wire.RoomInstance(instance, _options.Photon, roomName, instance.IsPrivate, occupancy.CountIn(instance.Id));

    public async Task<string> RoomNameAsync(long roomId, CancellationToken cancellationToken)
        => await db.Rooms.AsNoTracking().Where(r => r.Id == roomId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken)
           ?? string.Empty;

    /// <summary>
    /// Drops instances that have been sitting empty past the timeout. Without this the shared pool
    /// fills with abandoned rooms and matchmaking keeps routing players into dead Photon rooms.
    /// </summary>
    public async Task<int> ReapAsync(TimeSpan maxIdle, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - maxIdle;
        var candidates = await db.RoomInstances
            .Where(i => i.CreatedAt < cutoff && !i.IsInProgress)
            .ToListAsync(cancellationToken);

        var stale = candidates.Where(i => occupancy.CountIn(i.Id) == 0).ToList();
        if (stale.Count == 0) return 0;

        db.RoomInstances.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var instance in stale) occupancy.Forget(instance.Id);
        return stale.Count;
    }
}
