using Microsoft.AspNetCore.SignalR;

namespace RecEmu.Server.Realtime;

/// <summary>
/// Routes a serialized envelope to SignalR connections.
///
/// Connections are grouped per account id, which is what makes a push addressable: a relationship
/// change has to reach the affected players and nobody else, and rebuilding that audience per call
/// site is where pushes get dropped. Group membership is claimed once at connection time from the
/// token's subject claim, so it cannot be spoofed by the client's subscription calls.
/// </summary>
public sealed class SignalRPushSender(IHubContext<NotifyHub> hub) : IPushSender
{
    public const string GroupPrefix = "player:";

    public static string GroupFor(int accountId) => GroupPrefix + accountId;

    public Task SendToPlayerAsync(int accountId, string json, CancellationToken cancellationToken = default)
        => hub.Clients.Group(GroupFor(accountId)).SendAsync(NotifyHub.ClientMethod, json, cancellationToken);

    public async Task SendToPlayersAsync(IEnumerable<int> accountIds, string json, CancellationToken cancellationToken = default)
    {
        foreach (var accountId in accountIds.Distinct())
            await SendToPlayerAsync(accountId, json, cancellationToken);
    }

    public Task SendToAllAsync(string json, CancellationToken cancellationToken = default)
        => hub.Clients.All.SendAsync(NotifyHub.ClientMethod, json, cancellationToken);
}
