using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;

namespace RecEmu.Server.Auth;

public sealed record IssuedToken(string AccessToken, string RefreshToken, DateTime ExpiresAt, string Key);

/// <summary>
/// Mints and validates the bearer tokens the RecNet services accept.
///
/// Tokens are JWTs rather than opaque handles so any service in the deployment can validate them
/// without a shared store, which is what lets the whole stack scale out. The refresh token is a
/// separate opaque random value stored hashed: access tokens are not revocable by design, and the
/// client only ever exchanges the refresh token for a new pair.
/// </summary>
public sealed class TokenService(RecEmuOptionsAccessor options)
{
    // Fully qualified because the SigningKey property below shadows the SigningKey class in this
    // file's own scope.
    private readonly byte[] _key = Auth.SigningKey.Resolve(options.Value);

    public SigningCredentials Credentials => new(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);

    public SymmetricSecurityKey SecurityKey => new(_key);

    public IssuedToken Issue(Player player, string? loginToken = null)
    {
        var expires = DateTime.UtcNow.AddMinutes(options.Value.AccessTokenMinutes);
        var token = new JwtSecurityToken(
            issuer: options.Value.JwtIssuer,
            audience: options.Value.JwtIssuer,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, player.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, player.Id.ToString()),
                new Claim("deviceId", player.DeviceId),
            },
            notBefore: DateTime.UtcNow.AddSeconds(-30),
            expires: expires,
            signingCredentials: Credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);
        var refreshToken = NewRefreshToken();
        return new IssuedToken(accessToken, refreshToken, expires, loginToken ?? NewRefreshToken());
    }

    public static string NewRefreshToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Validates a bearer token and returns the account id, or null if it is not usable.</summary>
    public int? Validate(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            var principal = handler.ValidateToken(accessToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Value.JwtIssuer,
                ValidateAudience = true,
                ValidAudience = options.Value.JwtIssuer,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(_key),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            }, out _);

            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                          ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(subject, out var id) ? id : null;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// The HMAC signing key, resolved once per configured secret path.
///
/// Cached rather than re-read because the token service and the JWT bearer handler both need it,
/// and they are constructed independently: a key that was regenerated between those two
/// constructions would validate nothing, and the symptom — every request 401ing while issuance
/// reports success — is very hard to trace back here. The cache key includes the configured secret,
/// so changing it deliberately still takes effect without a restart of the process's assumptions.
/// </summary>
public static class SigningKey
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    public static byte[] Resolve(RecEmuOptionsAccessor accessor) => Resolve(accessor.Value);

    public static byte[] Resolve(RecEmuOptions options)
        => Cache.GetOrAdd($"{options.SecretFile}\n{options.JwtSecret}", _ => Generate(options));

    private static byte[] Generate(RecEmuOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.JwtSecret) &&
            Encoding.UTF8.GetByteCount(options.JwtSecret) >= 32)
        {
            return Encoding.UTF8.GetBytes(options.JwtSecret);
        }

        // Persist a generated key so tokens survive a restart; a rotating key would silently
        // invalidate every live session on each deploy.
        var path = options.SecretFile;
        if (string.IsNullOrWhiteSpace(path)) return RandomNumberGenerator.GetBytes(48);

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full))
        {
            var existing = File.ReadAllText(full).Trim();
            try { return Convert.FromBase64String(existing); }
            catch (FormatException) { /* fall through and regenerate */ }
        }

        var generated = RandomNumberGenerator.GetBytes(48);
        File.WriteAllText(full, Convert.ToBase64String(generated));
        return generated;
    }
}

/// <summary>Late-bound options, so the secret file path and generated key stay in one place.</summary>
public sealed class RecEmuOptionsAccessor(Configuration.RecEmuOptions value)
{
    public Configuration.RecEmuOptions Value { get; } = value;
}
