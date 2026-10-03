namespace RecEmu.Server.Configuration;

/// <summary>
/// The RecNet service-discovery map: the label the client knows a service by, mapped to the
/// subdomain it is reached at. The game fetches <c>{ label: "https://sub.domain" }</c> from the
/// apex/ns host and routes every later call by that table, so this is the contract's spine —
///
/// getting a label wrong here breaks exactly the subsystem behind it, silently, because the
/// client just falls back to its default URL.
/// </summary>
public static class ServiceCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Accounts"] = "accounts",
        ["AI"] = "ai",
        ["API"] = "api",
        ["Auth"] = "auth",
        ["BugReporting"] = "bugreporting",
        ["Cards"] = "cards",
        ["CDN"] = "cdn",
        ["Chat"] = "chat",
        ["Clubs"] = "clubs",
        ["CMS"] = "cms",
        ["Commerce"] = "commerce",
        ["Data"] = "data",
        ["DataCollection"] = "datacollection",
        ["Discovery"] = "discovery",
        ["Econ"] = "econ",
        ["GameLogs"] = "gamelogs",
        ["Geo"] = "geo",
        ["Images"] = "img",
        ["Leaderboard"] = "leaderboard",
        ["Link"] = "link",
        ["Lists"] = "lists",
        ["Matchmaking"] = "match",
        ["Moderation"] = "moderation",
        ["Notifications"] = "notify",
        ["PlatformNotifications"] = "platformnotifications",
        ["PlayerSettings"] = "playersettings",
        ["RoomComments"] = "roomcomments",
        ["RoomieIntegrations"] = "roomieintegrations",
        ["Rooms"] = "rooms",
        ["Storage"] = "storage",
        ["Strings"] = "strings",
        ["StringsCDN"] = "strings-cdn",
        ["Studio"] = "studio",
        ["Thorn"] = "thorn",
        ["Videos"] = "videos",
        ["WWW"] = "www",
    };

    /// <summary>Hosts that serve the discovery document itself, and are not listed inside it.</summary>
    public static readonly string[] NameserverHosts = { "ns", "" };

    public static IReadOnlyDictionary<string, string> BuildEndpoints(RecEmuOptions options)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, sub) in Labels)
        {
            var host = options.Subdomains.TryGetValue(sub, out var replacement) && !string.IsNullOrWhiteSpace(replacement)
                ? replacement
                : sub;
            result[label] = $"https://{host}.{options.Domain}";
        }
        return result;
    }

    /// <summary>All hosts the server answers on: the apex plus every advertised subdomain.</summary>
    public static IReadOnlySet<string> AllHosts(RecEmuOptions options)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { options.Domain };
        foreach (var url in BuildEndpoints(options).Values)
        {
            var uri = new Uri(url);
            hosts.Add(uri.Host);
        }
        return hosts;
    }

    /// <summary>
    /// The bare subdomain a request arrived on, e.g. "rooms" for rooms.rec.local. Returns null
    /// for the apex. Callers use it to route the handful of routes that genuinely differ per
    /// service host; everything else is served on every host on purpose, because the client's
    /// host assignment for some route families is only inferable from the binary, not provable.
    /// </summary>
    public static string? SubdomainOf(string host, RecEmuOptions options)
    {
        if (string.IsNullOrEmpty(host)) return null;
        host = host.Split(':')[0];
        if (host.Equals(options.Domain, StringComparison.OrdinalIgnoreCase)) return "";
        var suffix = "." + options.Domain;
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? host[..^suffix.Length] : null;
    }
}
