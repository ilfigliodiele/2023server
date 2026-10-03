using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace RecEmu.Server.Realtime;

/// <summary>
/// The push channel. The client opens a SignalR hub at <c>{notifyHost}/hub/v1</c> and registers
/// exactly one server method, <c>Notification</c>, taking a single string whose content is
/// <c>{"Id":…, "Msg":…}</c>.
///
/// That surface is narrow on purpose: naming the method <c>ReceiveNotification</c>, or sending an
/// object instead of a string, makes every push vanish with no error anywhere — the client simply
/// never dispatches them. So the shape is fixed here in one place and everything else funnels
/// through <see cref="NotificationDispatcher"/>.
/// </summary>
[Authorize]
public sealed class NotifyHub(ILogger<NotifyHub> logger) : Hub
{
    public const string ClientMethod = "Notification";

    private readonly ILogger<NotifyHub> _logger = logger;

    public override async Task OnConnectedAsync()
    {
        var raw = Context.User?.FindFirst("sub")?.Value
                  ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (int.TryParse(raw, out var id))
        {
            Context.Items["accountId"] = id;

            // Group membership is claimed here from the validated token, not from anything the
            // client sends. Joining later would mean every push in the gap is lost, and a client
            // that never called SubscribeToPlayers would silently receive nothing forever.
            await Groups.AddToGroupAsync(Context.ConnectionId, SignalRPushSender.GroupFor(id));
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// The only hub method the 2023 client calls. Subscriptions are advisory: presence is routed
    /// server-side by the friend graph, so honouring the list is an optimization and a wrong or
    /// stale list never blocks a push.
    /// </summary>
    public Task SubscribeToPlayers(SubscribeRequest request)
    {
        var accountId = ResolveAccountId();
        if (accountId is null) return Task.CompletedTask;

        Context.Items["subscribed"] = request?.PlayerIds ?? new List<int>();
        _logger.LogDebug("player {AccountId} subscribed to {Count} players", accountId, request?.PlayerIds?.Count ?? 0);
        return Task.CompletedTask;
    }

    /// <summary>Present in the client binary but never called; kept so the route surface matches.</summary>
    public Task UnsubscribeFromPlayers(SubscribeRequest request) => Task.CompletedTask;

    public Task SubscribeTo(string target) => Task.CompletedTask;

    public Task UnsubscribeFrom(string target) => Task.CompletedTask;

    private int? ResolveAccountId()
    {
        if (Context.Items.TryGetValue("accountId", out var cached) && cached is int parsed) return parsed;
        var raw = Context.User?.FindFirst("sub")?.Value ?? Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(raw, out var id) ? id : null;
    }
}

public sealed class SubscribeRequest
{
    public List<int>? PlayerIds { get; set; }
}

/// <summary>Sends one push to one connection group.</summary>
public interface IPushSender
{
    Task SendToPlayerAsync(int accountId, string json, CancellationToken cancellationToken = default);
    Task SendToPlayersAsync(IEnumerable<int> accountIds, string json, CancellationToken cancellationToken = default);
    Task SendToAllAsync(string json, CancellationToken cancellationToken = default);
}

/// <summary>
/// Names and ids of the push events.
///
/// The client builds one dispatch dictionary at startup and looks the envelope's <c>Id</c> up in it
/// with a plain, case-sensitive TryGetValue that returns silently on a miss. A wrong id is
/// therefore invisible on the server, on the wire and in the client log — the push just never
/// arrives. Most handlers are registered by enum ordinal, so the *number* is what has to go on the
/// wire; the handful registered by name are listed explicitly in <see cref="StringKeyed"/>.
/// </summary>
public static class NotificationId
{
    public const int AccountUpdate = 11;
    public const int PresenceUpdate = 12;
    public const int GameSessionUpdate = 13;
    public const int RoomUpdate = 15;
    public const int ModerationKick = 22;
    public const int ModerationUnkick = 23;
    public const int ChatMessageReceived = 90;
    public const int CommunityBoardUpdate = 95;

    /// <summary>
    /// The six ids the client registers under a bespoke string rather than the ordinal. Sending the
    /// number for one of these is the single most common reason "the push never arrives".
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> StringKeyed = new Dictionary<int, string>
    {
        [AccountUpdate] = "AccountUpdate",
        [PresenceUpdate] = "PresenceUpdate",
        [GameSessionUpdate] = "RoomInstanceUpdate",
        [RoomUpdate] = "RoomUpdate",
        [ChatMessageReceived] = "ChatMessageReceived",
        [CommunityBoardUpdate] = "CommunityBoardUpdate",
    };

    /// <summary>
    /// Room bans are announced as ModerationKick with IsBan set. The client registers no handler
    /// for the ban event id, so a "correct" ban push is a no-op on the receiving end.
    /// </summary>
    public static object ForWire(int id) => StringKeyed.TryGetValue(id, out var key) ? Encode(key) : id;

    public static object Encode(string key) => key;

    public static string Envelope(int id, object? message)
        => System.Text.Json.JsonSerializer.Serialize(
            JsonObjectShim.Envelope(id, message),
            Infrastructure.Json.Options);
}

internal static class JsonObjectShim
{
    // Id is boxed as object because the six string-keyed events put a name on the wire while the
    // rest put a number, and one dictionary type has to carry both.
    public static Dictionary<string, object?> Envelope(int id, object? message)
        => new(StringComparer.Ordinal)
        {
            ["Id"] = NotificationId.ForWire(id),
            ["Msg"] = message,
        };
}

/// <summary>
/// Turns domain events into wire pushes.
///
/// The envelope's Id is resolved here and nowhere else, so the numeric-vs-name rule is enforced in
/// one place instead of being re-derived at each call site.
/// </summary>
public sealed class NotificationDispatcher(IPushSender sender, ILogger<NotificationDispatcher> logger)
{
    private readonly IPushSender _sender = sender;
    private readonly ILogger<NotificationDispatcher> _logger = logger;

    public Task SendToPlayerAsync(int accountId, int id, object? message, CancellationToken cancellationToken = default)
    {
        Log(accountId, id);
        return _sender.SendToPlayerAsync(accountId, NotificationId.Envelope(id, message), cancellationToken);
    }

    public Task SendToPlayersAsync(IEnumerable<int> accountIds, int id, object? message, CancellationToken cancellationToken = default)
    {
        var targets = accountIds.Distinct().ToList();
        if (targets.Count == 0) return Task.CompletedTask;
        Log(targets.Count, id);
        return _sender.SendToPlayersAsync(targets, NotificationId.Envelope(id, message), cancellationToken);
    }

    public Task BroadcastAsync(int id, object? message, CancellationToken cancellationToken = default)
    {
        Log(-1, id);
        return _sender.SendToAllAsync(NotificationId.Envelope(id, message), cancellationToken);
    }

    /// <summary>
    /// Room bans ride on ModerationKick with IsBan. The dedicated ban id exists in the protocol
    /// but the client registers no handler for it, so sending it would drop the notification.
    /// </summary>
    public Task BanAsync(IEnumerable<int> accountIds, long roomId, string reason, CancellationToken cancellationToken = default)
        => SendToPlayersAsync(accountIds, NotificationId.ModerationKick, new Dictionary<string, object?>
        {
            ["AccountId"] = accountIds.FirstOrDefault(),
            ["RoomId"] = roomId,
            ["Reason"] = reason,
            ["IsBan"] = true,
        }, cancellationToken);

    private void Log(int target, int id)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("push id={Id} (wire {Wire}) to {Target}", id, NotificationId.ForWire(id), target);
    }
}
