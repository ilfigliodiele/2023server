namespace RecEmu.Server.Configuration;

/// <summary>
/// Everything an operator can change without touching code. Bound from the "RecEmu" section
/// of appsettings.json (RecEmu__Domain=... on the environment).
/// </summary>
public sealed class RecEmuOptions
{
    public const string SectionName = "RecEmu";

    /// <summary>Apex domain the service-discovery document is built from, e.g. "rec.local".</summary>
    public string Domain { get; set; } = "rec.local";

    /// <summary>
    /// Redirects a service to a different subdomain. Keyed by the *default* subdomain, and it
    /// moves both the advertised host and the virtual host the app listens for. Example:
    /// <c>{ "moderation": "api" }</c> points the client's Moderation calls at the api host.
    /// </summary>
    public Dictionary<string, string> Subdomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Client build the server reports as supported. Must match what players run.</summary>
    public string AppVersion { get; set; } = "20230317";

    /// <summary>Manifest the supported build corresponds to.</summary>
    public string AppManifest { get; set; } = "7859140924515540835";

    /// <summary>Symmetric key used to sign bearer tokens. &gt;= 32 chars, generated on first boot if empty.</summary>
    public string JwtSecret { get; set; } = string.Empty;

    /// <summary>Where a generated signing key is persisted so tokens survive a restart.</summary>
    public string SecretFile { get; set; } = "data/signing.key";

    public string JwtIssuer { get; set; } = "recemu";

    /// <summary>Access-token lifetime in minutes.</summary>
    public int AccessTokenMinutes { get; set; } = 60 * 24 * 30;

    /// <summary>
    /// When true, connections and unknown routes are accepted on any host, which makes local
    /// testing with a single hosts-file entry possible. Production deployments should keep the
    /// per-host allowlist on so a mis-typed host fails loudly instead of silently 404ing.
    /// </summary>
    public bool ServeAllHosts { get; set; } = true;

    /// <summary>Self-registering accounts on launch. Disable and hand out signup codes instead.</summary>
    public bool AllowSignup { get; set; } = true;

    /// <summary>
    /// When true the very first account created becomes admin. Turn off once your admin account
    /// exists, otherwise a race on a fresh database hands admin to a stranger.
    /// </summary>
    public bool FirstAccountIsAdmin { get; set; } = true;

    /// <summary>Accounts need a password to log in. Off means device-id keyed accounts.</summary>
    public bool RequirePassword { get; set; }

    /// <summary>Everyone is a friend of everyone. Convenient for small friend groups.</summary>
    public bool GlobalFriends { get; set; } = true;

    public int DefaultRoomCapacity { get; set; } = 8;
    public int MaxRoomCapacity { get; set; } = 40;

    /// <summary>Where uploaded blobs live. Empty means a folder under the content root.</summary>
    public string? BlobRoot { get; set; }

    /// <summary>Public base URL of the blob store, advertised in image/CDN responses.</summary>
    public string BlobBaseUrl { get; set; } = string.Empty;

    public PhotonOptions Photon { get; set; } = new();
}

public sealed class PhotonOptions
{
    /// <summary>Photon Realtime application id the client is patched to use.</summary>
    public string RealtimeAppId { get; set; } = "ce298166-7e52-4c47-a1c2-0ce4b7a7cfcb";

    /// <summary>Photon Voice application id the client is patched to use.</summary>
    public string VoiceAppId { get; set; } = "5fa7359e-b671-4910-8226-cb539cdae485";

    /// <summary>Region handed out in roomInstance payloads.</summary>
    public string Region { get; set; } = "localhost";

    /// <summary>Realtime (TCP/WebSocket) port of the Photon server.</summary>
    public int Port { get; set; } = 5000;
}
