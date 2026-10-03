using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Data;

namespace RecEmu.Server.Auth;

/// <summary>
/// Resolves the calling player from the bearer token.
///
/// Every route that is not explicitly anonymous goes through here. The id claim is trusted (the
/// signature was already verified), but the player row is re-read each request so a deletion or a
/// ban takes effect immediately instead of at token expiry — a banned player otherwise keeps a
/// valid token for the whole access-token lifetime.
///
/// The dependency is IHttpContextAccessor rather than the concrete type: only the interface is
/// guaranteed by AddHttpContextAccessor, and depending on the concrete one resolves to a DI
/// failure at the first authenticated request rather than at startup.
/// </summary>
public sealed class CurrentPlayerAccessor(RecEmuDb db, TokenService tokens, IHttpContextAccessor accessor)
{
    public const string ItemKey = "recemu.player";

    public async Task<Player?> GetAsync(CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext;
        if (context is null) return null;

        if (context.Items.TryGetValue(ItemKey, out var cached))
            return cached as Player;

        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var id = tokens.Validate(header["Bearer ".Length..].Trim());
        if (id is null) return null;

        var player = await db.Players
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);

        if (player is { IsBanned: true } || player is { IsDeleted: true })
            return null;

        context.Items[ItemKey] = player;
        return player;
    }

    /// <summary>Throws 401 when there is no usable caller. Use on routes that must not be anonymous.</summary>
    public async Task<Player> RequireAsync(CancellationToken cancellationToken = default)
        => await GetAsync(cancellationToken) ?? throw new UnauthorizedException("no player on this request");
}

/// <summary>401 short-circuits the pipeline instead of surfacing as a 500.</summary>
public sealed class UnauthorizedException(string message) : Exception(message);

/// <summary>
/// Password hashing. PBKDF2-SHA256 with a per-password salt: no external dependency, and the
/// stored format carries the iteration count so it can be raised later without invalidating
/// existing hashes.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string? stored, string? password)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(password)) return false;
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Passwords the client can send are short; anything longer is a mistake or an attack.</summary>
    public static bool IsAcceptable(string? password)
        => !string.IsNullOrWhiteSpace(password) && password.Length is >= 6 and <= 128;
}
