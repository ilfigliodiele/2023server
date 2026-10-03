using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Login, token exchange and cached-login lookup.
///
/// The client treats Rec Room's auth as OAuth-shaped but not OAuth: it posts urlencoded form data
/// with a fixed public client_id/client_secret, and picks a behaviour from the grant_type rather
/// than from a spec. Every grant is handled explicitly here. That matters more than it looks —
/// falling through to a generic "resolve by device id" branch makes grant_type=login_token sign
/// in the wrong account and makes grant_type=device_code hand out a token before the remote
/// approval that was supposed to authorize it, which is the whole point of the device flow.
/// </summary>
public static class AuthEndpoints
{
    public const string ClientId = "recroom";
    public const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";

    public static void Map(WebApplication app)
    {
        MapToken(app);
        MapDeviceAuthorization(app);
        MapCachedLogins(app);
        MapMisc(app);
    }

    private static void MapToken(WebApplication app)
    {
        // The same handler is mounted on both hosts: depending on the client build the token
        // exchange is issued against auth or against api.
        var handler = async (
            HttpContext http,
            RecEmuDb db,
            TokenService tokens,
            RecEmuOptionsAccessor options,
            PresenceService presence,
            CancellationToken cancellationToken) =>
        {
            var grant = await http.ReadStringAsync("grant_type") ?? string.Empty;
            var deviceId = (await http.ReadStringAsync("device_id", "deviceId"))?.Trim() ?? string.Empty;
            var platform = await http.ReadIntAsync("platform") ?? 0;
            var platformId = (await http.ReadStringAsync("platform_id", "platformId")) ?? string.Empty;
            var deviceClass = (await http.ReadStringAsync("device_class", "deviceClass")) ?? string.Empty;
            var username = (await http.ReadStringAsync("username", "userName")) ?? string.Empty;
            var password = (await http.ReadStringAsync("password") ?? string.Empty);

            switch (grant)
            {
                case "refresh_token":
                    return await RefreshAsync(http, db, tokens, presence, cancellationToken);

                case "cached_login":
                    return await CachedLoginAsync(http, db, tokens, options, presence, deviceId, platform, platformId, cancellationToken);

                case "login_token":
                    return await LoginTokenAsync(http, db, tokens, presence, cancellationToken);

                case DeviceCodeGrant:
                    return await DeviceCodeAsync(http, db, tokens, presence, cancellationToken);

                case "password":
                    return await PasswordAsync(db, tokens, options, presence, deviceId, platform, platformId, username, password, cancellationToken);

                case "create_account":
                    return await CreateAccountAsync(db, tokens, options, presence, deviceId, platform, platformId, username, password, cancellationToken);

                default:
                    // An unknown grant still resolves against the device id, which is how some
                    // client builds bootstrap a fresh installation.
                    return await CachedLoginAsync(http, db, tokens, options, presence, deviceId, platform, platformId, cancellationToken);
            }
        };

        app.MapPost("/connect/token", handler);
        app.MapPost("/api/connect/token", handler);
    }

    private static IResult TokenError(string error, string description, int status = 400)
        => Results.Json(new Dictionary<string, object?>
        {
            ["Error"] = error,
            ["error"] = error,
            ["error_description"] = description,
            ["Error_description"] = description,
        }, Json.Options, statusCode: status);

    private static async Task<IResult> IssueAsync(
        Player player, string deviceId, int platform, string platformId, bool requirePassword,
        RecEmuDb db, TokenService tokens, PresenceService presence, CancellationToken cancellationToken)
    {
        var issued = tokens.Issue(player);

        db.RefreshTokens.Add(new RefreshToken
        {
            PlayerId = player.Id,
            TokenHash = TokenService.HashToken(issued.RefreshToken),
            DeviceId = deviceId,
            Platform = platform,
            PlatformId = platformId,
            RequirePassword = requirePassword,
            LoginToken = issued.Key,
        });
        player.LastLoginTime = DateTime.UtcNow;
        player.Platform = platform;
        if (!string.IsNullOrWhiteSpace(platformId)) player.PlatformId = platformId;
        player.DeviceId = string.IsNullOrWhiteSpace(deviceId) ? player.DeviceId : deviceId;
        presence.SetOnline(player.Id, true);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Json(new Dictionary<string, object?>
        {
            ["access_token"] = issued.AccessToken,
            ["Access_token"] = issued.AccessToken,
            ["refresh_token"] = issued.RefreshToken,
            ["Refresh_token"] = issued.RefreshToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)(issued.ExpiresAt - DateTime.UtcNow).TotalSeconds,
            ["Key"] = issued.Key,
            ["key"] = issued.Key,
            ["scope"] = "openid profile offline_access",
        }, Json.Options);
    }

    private static async Task<IResult> PasswordAsync(
        RecEmuDb db, TokenService tokens, RecEmuOptionsAccessor options, PresenceService presence,
        string deviceId, int platform, string platformId, string username, string password,
        CancellationToken cancellationToken)
    {
        var player = string.IsNullOrWhiteSpace(username)
            ? null
            : await db.Players.FirstOrDefaultAsync(p => p.Username == username && !p.IsDeleted, cancellationToken);

        if (player is null)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return TokenError("invalid_grant", "no device id and no matching account");
            if (!options.Value.AllowSignup)
                return TokenError("invalid_grant", "account creation is disabled on this server");
            player = await Accounts.GetOrCreateByDeviceAsync(db, options, deviceId, username, cancellationToken);
        }
        else if (options.Value.RequirePassword || player.HasPassword)
        {
            if (!PasswordHasher.Verify(player.PasswordHash, password))
                return TokenError("invalid_grant", "invalid username or password");
        }

        if (player.IsBanned) return TokenError("invalid_grant", "account is banned", 403);

        return await IssueAsync(player, deviceId, platform, platformId, options.Value.RequirePassword, db, tokens, presence, cancellationToken);
    }

    private static async Task<IResult> CreateAccountAsync(
        RecEmuDb db, TokenService tokens, RecEmuOptionsAccessor options, PresenceService presence,
        string deviceId, int platform, string platformId, string username, string password,
        CancellationToken cancellationToken)
    {
        if (!options.Value.AllowSignup)
            return TokenError("invalid_grant", "account creation is disabled on this server");
        if (string.IsNullOrWhiteSpace(deviceId))
            return TokenError("invalid_request", "device_id is required to create an account");

        var player = await Accounts.GetOrCreateByDeviceAsync(db, options, deviceId, username, cancellationToken);

        if (!string.IsNullOrWhiteSpace(password) && PasswordHasher.IsAcceptable(password))
        {
            player.PasswordHash = PasswordHasher.Hash(password);
            player.HasPassword = true;
            await db.SaveChangesAsync(cancellationToken);
        }

        return await IssueAsync(player, deviceId, platform, platformId, options.Value.RequirePassword, db, tokens, presence, cancellationToken);
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext http, RecEmuDb db, TokenService tokens, PresenceService presence, CancellationToken cancellationToken)
    {
        var presented = (await http.ReadStringAsync("refresh_token", "Refresh_token")) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(presented)) return TokenError("invalid_request", "refresh_token is required");

        var hash = TokenService.HashToken(presented);
        var stored = await db.RefreshTokens
            .Include(t => t.Player)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, cancellationToken);

        if (stored is null) return TokenError("invalid_grant", "unknown or already used refresh token");

        // Rotate: presenting the same token twice is a replay, so the first use invalidates it.
        stored.RevokedAt = DateTime.UtcNow;

        var issued = tokens.Issue(stored.Player, stored.LoginToken);
        db.RefreshTokens.Add(new RefreshToken
        {
            PlayerId = stored.PlayerId,
            TokenHash = TokenService.HashToken(issued.RefreshToken),
            DeviceId = stored.DeviceId,
            Platform = stored.Platform,
            PlatformId = stored.PlatformId,
            RequirePassword = stored.RequirePassword,
            LoginToken = issued.Key,
        });

        stored.Player.LastLoginTime = DateTime.UtcNow;
        presence.SetOnline(stored.PlayerId, true);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Json(new Dictionary<string, object?>
        {
            ["access_token"] = issued.AccessToken,
            ["refresh_token"] = issued.RefreshToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)(issued.ExpiresAt - DateTime.UtcNow).TotalSeconds,
            ["Key"] = issued.Key,
        }, Json.Options);
    }

    private static async Task<IResult> CachedLoginAsync(
        HttpContext http, RecEmuDb db, TokenService tokens, RecEmuOptionsAccessor options, PresenceService presence,
        string deviceId, int platform, string platformId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return TokenError("invalid_request", "device_id is required");

        var player = await db.Players.FirstOrDefaultAsync(p => p.DeviceId == deviceId && !p.IsDeleted && !p.IsBanned, cancellationToken);
        if (player is null)
        {
            // A device with no account yet is a first launch. Only bootstrap one while signups
            // are open; afterwards the operator's signup codes are the way in.
            if (!options.Value.AllowSignup)
                return TokenError("invalid_grant", "no account for this device and registration is closed");
            player = await Accounts.GetOrCreateByDeviceAsync(db, options, deviceId, null, cancellationToken);
        }

        return await IssueAsync(player, deviceId, platform, platformId, false, db, tokens, presence, cancellationToken);
    }

    private static async Task<IResult> LoginTokenAsync(
        HttpContext http, RecEmuDb db, TokenService tokens, PresenceService presence, CancellationToken cancellationToken)
    {
        var token = (await http.ReadStringAsync("login_token")) ?? string.Empty;
        var accountId = await http.ReadIntAsync("account_id");

        if (string.IsNullOrWhiteSpace(token) || accountId is null)
            return TokenError("invalid_request", "login_token and account_id are required");

        var loginToken = await db.LoginTokens
            .FirstOrDefaultAsync(t => t.Token == token && t.PlayerId == accountId && t.ConsumedAt == null, cancellationToken);

        if (loginToken is null) return TokenError("invalid_grant", "login token is invalid or already used");
        if (loginToken.ExpiresAt < DateTime.UtcNow) return TokenError("invalid_grant", "login token has expired");

        var player = await db.Players.FirstOrDefaultAsync(p => p.Id == accountId && !p.IsDeleted && !p.IsBanned, cancellationToken);
        if (player is null) return TokenError("invalid_grant", "no such account");

        loginToken.ConsumedAt = DateTime.UtcNow;
        return await IssueAsync(player, player.DeviceId, player.Platform, player.PlatformId, false, db, tokens, presence, cancellationToken);
    }

    private static async Task<IResult> DeviceCodeAsync(
        HttpContext http, RecEmuDb db, TokenService tokens, PresenceService presence, CancellationToken cancellationToken)
    {
        var deviceCode = (await http.ReadStringAsync("device_code")) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceCode)) return TokenError("invalid_request", "device_code is required");

        var pending = await db.DeviceAuthorizations.FirstOrDefaultAsync(d => d.DeviceCode == deviceCode, cancellationToken);
        if (pending is null) return TokenError("invalid_grant", "unknown device code");
        if (pending.ExpiresAt < DateTime.UtcNow) return TokenError("expired_token", "device code has expired");
        if (pending.Denied) return TokenError("access_denied", "the request was denied");
        if (pending.ApprovedPlayerId is null) return TokenError("authorization_pending", "waiting for approval");

        var player = await db.Players.FirstOrDefaultAsync(p => p.Id == pending.ApprovedPlayerId && !p.IsDeleted, cancellationToken);
        if (player is null) return TokenError("invalid_grant", "approving account no longer exists");

        db.DeviceAuthorizations.Remove(pending);
        return await IssueAsync(player, player.DeviceId, player.Platform, player.PlatformId, false, db, tokens, presence, cancellationToken);
    }

    private static void MapDeviceAuthorization(WebApplication app)
    {
        app.MapPost("/connect/deviceauthorization", async (
            HttpContext http, RecEmuDb db, RecEmuOptionsAccessor options, CancellationToken cancellationToken) =>
        {
            var deviceCode = TokenService.NewRefreshToken();
            var userCode = RandomNumberGenerator.GetBytes(5)
                .Select(b => (char)('A' + b % 26))
                .ToArray();
            var code = new string(userCode);

            db.DeviceAuthorizations.Add(new DeviceAuthorization
            {
                DeviceCode = deviceCode,
                UserCode = code,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync(cancellationToken);

            var verificationHost = options.Value.Subdomains.TryGetValue("site", out var site) ? site : "site";
            var verificationUri = $"https://{verificationHost}.{options.Value.Domain}/device";

            // Wire keys here are snake_case, unlike the rest of the protocol.
            return Results.Json(new Dictionary<string, object?>
            {
                ["device_code"] = deviceCode,
                ["user_code"] = code,
                ["verification_uri"] = verificationUri,
                ["verification_uri_complete"] = $"{verificationUri}?user_code={code}",
                ["expires_in"] = 600,
                ["interval"] = 5,
            }, Json.Options);
        });
    }

    private static void MapCachedLogins(WebApplication app)
    {
        app.MapGet("/cachedlogin/forplatformid/{platform:int}/{platformId}", async (
            int platform, string platformId, RecEmuDb db, CancellationToken cancellationToken) =>
        {
            var player = await db.Players
                .FirstOrDefaultAsync(p => p.Platform == platform && p.PlatformId == platformId && !p.IsDeleted, cancellationToken);
            return Results.Json(new List<object?> { player is null ? null : CachedLoginRow(player) }, Json.Options);
        });

        app.MapPost("/cachedlogin/forplatformids", async (HttpContext http, RecEmuDb db, CancellationToken cancellationToken) =>
        {
            // The client sends repeated `id=` fields, one per platform id, and no platform field.
            var ids = (await http.ReadValuesAsync("id", "platformIds", "PlatformIds"))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
            if (ids.Count == 0) return Results.Json(new List<object?>(), Json.Options);

            var platform = await http.ReadIntAsync("platform") ?? 0;
            var players = await db.Players
                .Where(p => ids.Contains(p.PlatformId) && !p.IsDeleted)
                .ToListAsync(cancellationToken);

            return Results.Json(players
                .Where(p => platform == 0 || p.Platform == platform)
                .Select(p => (object?)CachedLoginRow(p))
                .ToList(), Json.Options);
        });

        app.MapPost("/cachedlogin/migrate", async (HttpContext http, RecEmuDb db, CancellationToken cancellationToken) =>
        {
            var legacy = (await http.ReadStringAsync("legacyPlatformId")) ?? string.Empty;
            var replacement = (await http.ReadStringAsync("newPlatformId")) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(legacy) || string.IsNullOrWhiteSpace(replacement)) return Bare.Ok();

            var players = await db.Players.Where(p => p.PlatformId == legacy).ToListAsync(cancellationToken);
            foreach (var player in players) player.PlatformId = replacement;
            await db.SaveChangesAsync(cancellationToken);
            return Bare.Ok();
        });

        app.MapGet("/cachedlogin/current", async (CurrentPlayerAccessor current, CancellationToken cancellationToken) =>
        {
            var player = await current.GetAsync(cancellationToken);
            return Results.Json(new List<object?> { player is null ? null : CachedLoginRow(player) }, Json.Options);
        });

        app.MapDelete("/cachedlogin/current", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken cancellationToken) =>
        {
            // "Forget this account": drop the platform binding so the cached-login list no longer
            // offers it, without destroying the account itself.
            var player = await current.GetAsync(cancellationToken);
            if (player is null) return Bare.Ok();

            var tracked = await db.Players.FirstAsync(p => p.Id == player.Id, cancellationToken);
            tracked.PlatformId = string.Empty;
            tracked.LoginLock = null;
            await db.SaveChangesAsync(cancellationToken);
            return Bare.Ok();
        });
    }

    /// <summary>LBNJFPOLCDL/MDGJJCFGJNF. LastLoginTime is non-nullable in the client reader.</summary>
    private static Dictionary<string, object?> CachedLoginRow(Player player) => Obj.Create(
        ("Platform", player.Platform),
        ("PlatformId", player.PlatformId),
        ("AccountId", player.Id),
        ("LastLoginTime", Wire.Iso(player.LastLoginTime)),
        ("RequirePassword", player.HasPassword),
        ("RefreshToken", string.Empty));

    private static void MapMisc(WebApplication app)
    {
        app.MapGet("/eac/challenge", () =>
        {
            // Valid base64: the client feeds this straight into its anti-cheat challenge-response
            // generator, so a non-base64 body fails the handshake before login is even attempted.
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Results.Text(System.Text.Json.JsonSerializer.Serialize(Convert.ToBase64String(bytes), Json.Options), "application/json");
        });

        app.MapPost("/account/me/createlogintoken", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken cancellationToken) =>
        {
            var player = await current.GetAsync(cancellationToken);
            if (player is null) return Results.Unauthorized();

            var token = TokenService.NewRefreshToken();
            db.LoginTokens.Add(new LoginToken { PlayerId = player.Id, Token = token, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
            await db.SaveChangesAsync(cancellationToken);

            // A bare JSON string, not an object: the client reads this as a string and its strict
            // reader throws on '{', which breaks every "open on the web" link.
            return Bare.Text(token);
        });

        app.MapPost("/account/me/remoteauth", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken cancellationToken) =>
        {
            var player = await current.GetAsync(cancellationToken);
            if (player is null) return Results.Unauthorized();

            var code = (await http.ReadStringAsync("code", "user_code")) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code)) return Results.Unauthorized();

            var pending = await db.DeviceAuthorizations.FirstOrDefaultAsync(d => d.UserCode == code, cancellationToken);
            if (pending is null) return Results.Unauthorized();

            pending.ApprovedPlayerId = player.Id;
            await db.SaveChangesAsync(cancellationToken);
            return Bare.Ok();
        });

        app.MapPost("/api/account/me/remoteauth", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken cancellationToken) =>
        {
            var player = await current.GetAsync(cancellationToken);
            if (player is null) return Results.Unauthorized();

            var code = (await http.ReadStringAsync("code", "user_code")) ?? string.Empty;
            var pending = await db.DeviceAuthorizations.FirstOrDefaultAsync(d => d.UserCode == code, cancellationToken);
            if (pending is null) return Results.Unauthorized();

            pending.ApprovedPlayerId = player.Id;
            await db.SaveChangesAsync(cancellationToken);
            return Bare.Ok();
        });

        app.MapPost("/api/ageverification/generateCode", () =>
        {
            var digits = RandomNumberGenerator.GetBytes(6).Select(b => (char)('0' + b % 10)).ToArray();
            return Bare.Text(new string(digits));
        });

        app.MapGet("/api/banappeal/generateCode", () =>
        {
            var characters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = RandomNumberGenerator.GetBytes(8);
            var code = new string(bytes.Select(b => characters[b % characters.Length]).ToArray());
            return Bare.Text(code);
        });
    }
}
