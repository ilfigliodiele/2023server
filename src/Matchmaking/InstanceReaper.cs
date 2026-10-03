namespace RecEmu.Server.Matchmaking;

/// <summary>
/// Periodically retires abandoned room instances. Runs in-process because instance liveness is
/// already process-local (see <see cref="OccupancyTracker"/>); a multi-node deployment needs this
/// to be a shared-state reaper instead.
/// </summary>
public sealed class InstanceReaper(RoomInstanceService instances, ILogger<InstanceReaper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var reaped = await instances.ReapAsync(MaxIdle, stoppingToken);
                if (reaped > 0) logger.LogInformation("reaped {Count} idle room instances", reaped);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "instance reaper pass failed");
            }
        }
    }
}
