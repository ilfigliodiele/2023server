using System.Collections.Concurrent;

namespace RecEmu.Server.Matchmaking;

/// <summary>
/// Who is in which room instance right now.
///
/// Photon is authoritative for membership, but the HTTP layer still needs counts for matchmaking
/// (to stop handing players a full instance) and for instance reaping. Round-tripping to Photon on
/// every one of those would make the HTTP path depend on the realtime path's latency, so counts are
/// mirrored here from the presence heartbeat that already runs, and are cheap to be slightly stale.
/// </summary>
public sealed class OccupancyTracker
{
    private readonly ConcurrentDictionary<long, int> _counts = new();

    public void Join(long instanceId) => _counts.AddOrUpdate(instanceId, 1, (_, count) => count + 1);

    public void Leave(long instanceId)
        => _counts.AddOrUpdate(instanceId, 0, (_, count) => Math.Max(0, count - 1));

    public int CountIn(long instanceId) => _counts.TryGetValue(instanceId, out var count) ? count : 0;

    public bool IsOccupied(long instanceId) => CountIn(instanceId) > 0;

    public bool AnyOccupied() => _counts.Values.Any(v => v > 0);

    public IReadOnlyDictionary<long, int> Snapshot() => _counts.ToDictionary(x => x.Key, x => x.Value);

    public void Forget(long instanceId) => _counts.TryRemove(instanceId, out _);
}
