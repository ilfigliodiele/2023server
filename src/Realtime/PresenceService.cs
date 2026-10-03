using System.Collections.Concurrent;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Realtime;

/// <summary>
/// Who is online, and where.
///
/// This is deliberately in-memory while accounts, rooms and bans live in the database. Presence is
/// the one RecNet subsystem that is inherently per-process — a player is connected to exactly one
/// server — and persisting it would buy nothing but write amplification on the heartbeat path.
/// Anything that must survive a restart (preferences, friendship) is written through to the player
/// row by the endpoint that changes it, not here.
/// </summary>
public sealed class PresenceService(RecEmuOptionsAccessor options, ILogger<PresenceService> logger)
{
    private sealed record PlayerState(
        int AccountId,
        string DeviceClass,
        int StatusVisibility,
        int VrMovementMode,
        bool AvoidJuniors,
        string? LoginLock,
        DateTime ConnectedAt,
        DateTime LastHeartbeat);

    private readonly ConcurrentDictionary<int, PlayerState> _online = new();
    private readonly RecEmuOptions _options = options.Value;
    private readonly ILogger<PresenceService> _logger = logger;

    /// <summary>Room instance each online player currently occupies, for the friend presence push.</summary>
    private readonly ConcurrentDictionary<int, long> _roomOf = new();

    public IReadOnlyCollection<int> OnlineAccountIds => _online.Keys.ToList();

    public bool IsOnline(int accountId) => _online.ContainsKey(accountId);

    public long? RoomInstanceOf(int accountId)
        => _roomOf.TryGetValue(accountId, out var instanceId) ? instanceId : null;

    public int ConnectedCount => _online.Count;

    /// <summary>
    /// Registers a connection and reports whether the caller was already online. The exclusive
    /// login route turns a true into 409, which is how the client's "already logged in somewhere
    /// else" branch gets triggered.
    /// </summary>
    public bool TryConnect(int accountId, string loginLock, string deviceClass, out bool displaced)
    {
        displaced = false;
        var takenOver = false;
        var now = DateTime.UtcNow;
        _online.AddOrUpdate(accountId,
            _ => new PlayerState(accountId, deviceClass, 0, 0, false, loginLock, now, now),
            (_, existing) =>
            {
                // A reconnect of the same session is not a conflict; only a different
                // LoginLock means somebody else took the account.
                if (!string.IsNullOrEmpty(loginLock) && loginLock == existing.LoginLock) return existing;
                takenOver = true;
                return new PlayerState(accountId, deviceClass, existing.StatusVisibility, existing.VrMovementMode, existing.AvoidJuniors, loginLock, now, now);
            });
        displaced = takenOver;
        return true;
    }

    public void Heartbeat(int accountId, string loginLock)
    {
        _online.AddOrUpdate(accountId,
            _ => new PlayerState(accountId, string.Empty, 0, 0, false, loginLock, DateTime.UtcNow, DateTime.UtcNow),
            (_, existing) => existing with { LastHeartbeat = DateTime.UtcNow, LoginLock = loginLock });
    }

    public void Disconnect(int accountId, string loginLock)
    {
        _online.AddOrUpdate(accountId,
            _ => new PlayerState(accountId, string.Empty, 0, 0, false, loginLock, DateTime.UtcNow, DateTime.UtcNow),
            (_, existing) => string.IsNullOrEmpty(loginLock) || loginLock == existing.LoginLock
                ? existing with { LoginLock = null }
                : existing);
    }

    public void SetStatusVisibility(int accountId, int value)
        => _online.AddOrUpdate(accountId,
            _ => new PlayerState(accountId, string.Empty, value, 0, false, null, DateTime.UtcNow, DateTime.UtcNow),
            (_, existing) => existing with { StatusVisibility = value });

    public void SetVrMovementMode(int accountId, int value)
        => _online.AddOrUpdate(accountId,
            _ => new PlayerState(accountId, string.Empty, 0, value, false, null, DateTime.UtcNow, DateTime.UtcNow),
            (_, existing) => existing with { VrMovementMode = value });

    public void SetAvoidJuniors(int accountId, bool value)
        => _online.AddOrUpdate(accountId,
            _ => new PlayerState(accountId, string.Empty, 0, 0, value, null, DateTime.UtcNow, DateTime.UtcNow),
            (_, existing) => existing with { AvoidJuniors = value });

    public void SetRoom(int accountId, long? roomInstanceId)
    {
        if (roomInstanceId is null) _roomOf.TryRemove(accountId, out _);
        else _roomOf[accountId] = roomInstanceId.Value;
    }

    /// <summary>
    /// Accounts currently recorded as occupying an instance.
    ///
    /// Chat fan-out needs "everyone in the room", and Photon is the only thing that actually knows
    /// the authoritative list. This reads the presence mirror instead of asking Photon because the
    /// chat path runs per message: a Photon round-trip per message would couple chat throughput to
    /// realtime latency and drop messages when Photon is slow. The mirror is updated by the
    /// heartbeat the client already sends, so it is at most one heartbeat stale — and a stale
    /// answer costs one missing live reader, not a lost message.
    /// </summary>
    public List<int> OccupantsOf(long instanceId)
        => _roomOf.Where(pair => pair.Value == instanceId).Select(pair => pair.Key).ToList();

    public void RecordRegionPings(int accountId, IReadOnlyDictionary<string, int> pings)
    {
        if (pings.Count == 0) return;
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("player {AccountId} region pings: {Pings}", accountId, string.Join(",", pings.Select(p => $"{p.Key}={p.Value}")));
    }

    /// <summary>Preferences, falling back to the stored column when the player is offline.</summary>
    public int StatusVisibilityOf(Player player)
        => _online.TryGetValue(player.Id, out var state) ? state.StatusVisibility : player.StatusVisibility;

    public int VrMovementModeOf(Player player)
        => _online.TryGetValue(player.Id, out var state) ? state.VrMovementMode : player.VrMovementMode;

    public bool AvoidJuniorsOf(Player player)
        => _online.TryGetValue(player.Id, out var state) ? state.AvoidJuniors : player.AvoidJuniors;

    /// <summary>
    /// The presence payload the heartbeat route returns. appVersion is sent as the *string* the
    /// supported build reports: the client compares it against its own and marks presence
    /// out-of-sync, which shows up as a permanent [VERSION MISMATCH] tag next to every player.
    /// </summary>
    public Dictionary<string, object?> HeartbeatPayload(Player player, long roomInstanceId, long roomId, long subRoomId)
    {
        var state = _online.TryGetValue(player.Id, out var online) ? online : null;
        return Obj.Create(
            ("playerId", player.Id),
            ("statusVisibility", StatusVisibilityOf(player)),
            ("deviceClass", state?.DeviceClass ?? string.Empty),
            ("vrMovementMode", VrMovementModeOf(player)),
            ("roomInstanceId", roomInstanceId),
            ("roomId", roomId),
            ("subRoomId", subRoomId),
            ("isOnline", true),
            ("appVersion", _options.AppVersion));
    }

    /// <summary>Presence for a remote player, as broadcast to friends.</summary>
    public Dictionary<string, object?> PublicPayload(Player player, long? roomInstanceId, long roomId, long subRoomId)
    {
        var state = _online.TryGetValue(player.Id, out var online) ? online : null;
        return Obj.Create(
            ("playerId", player.Id),
            ("statusVisibility", StatusVisibilityOf(player)),
            ("deviceClass", state?.DeviceClass ?? string.Empty),
            ("vrMovementMode", VrMovementModeOf(player)),
            ("roomInstanceId", roomInstanceId),
            ("roomId", roomId),
            ("subRoomId", subRoomId),
            ("isOnline", state is not null),
            ("appVersion", _options.AppVersion),
            ("lastSeenTime", player.LastSeenTime?.ToString("o") ?? player.LastLoginTime.ToString("o")));
    }

    public void SetOnline(int accountId, bool online)
    {
        if (!online)
        {
            _online.TryRemove(accountId, out _);
            _roomOf.TryRemove(accountId, out _);
            return;
        }
        _online.TryAdd(accountId, new PlayerState(accountId, string.Empty, 0, 0, false, null, DateTime.UtcNow, DateTime.UtcNow));
    }
}
