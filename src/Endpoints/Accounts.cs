using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;

namespace RecEmu.Server.Endpoints;

/// <summary>Account creation and the shared lookup helpers the other subsystems need.</summary>
public static class Accounts
{
    private static readonly string[] Adjectives =
    {
        "Brave", "Calm", "Clever", "Cosmic", "Crimson", "Curious", "Dapper", "Eager",
        "Fuzzy", "Gentle", "Golden", "Happy", "Jolly", "Lucky", "Merry", "Nimble",
        "Quiet", "Rapid", "Silver", "Sunny", "Swift", "Tiny", "Witty", "Zesty",
    };

    private static readonly string[] Nouns =
    {
        "Badger", "Comet", "Dolphin", "Falcon", "Fox", "Gecko", "Hedgehog", "Ibis",
        "Jaguar", "Koala", "Lynx", "Meerkat", "Narwhal", "Otter", "Panther", "Quokka",
        "Raccoon", "Salamander", "Tapir", "Urchin", "Vicuna", "Walrus", "Yak", "Zebu",
    };

    public static IReadOnlyList<string> NameAdjectives => Adjectives;
    public static IReadOnlyList<string> NameNouns => Nouns;

    /// <summary>
    /// Resolves the account for a device id, creating one on first launch.
    ///
    /// Identity here is the per-install device id the client generates, not a verified platform
    /// account. That is a deliberate consequence of how the 2023 client works: it can be patched
    /// and side-loaded, so platform identity proves nothing. It does mean anyone who can reach
    /// the server with a patched client can get an account — the mitigation is turning signups off
    /// and issuing codes, not pretending the account is authenticated.
    /// </summary>
    public static async Task<Player> GetOrCreateByDeviceAsync(
        RecEmuDb db, RecEmuOptionsAccessor? options, string deviceId, string? requestedName,
        CancellationToken cancellationToken)
    {
        var existing = await db.Players.FirstOrDefaultAsync(p => p.DeviceId == deviceId && !p.IsDeleted, cancellationToken);
        if (existing is not null) return existing;

        // The first account on a fresh database owns the server. Operators are advised to create
        // it themselves before exposing the server, because this is a race and nothing else
        // guards it.
        var isFirst = !await db.Players.AnyAsync(cancellationToken);

        var player = new Player
        {
            DeviceId = deviceId,
            Username = await AllocateUsernameAsync(db, requestedName, cancellationToken),
            DisplayName = string.Empty,
            CreatedAt = DateTime.UtcNow,
            LastLoginTime = DateTime.UtcNow,
            IsAdmin = isFirst && (options?.Value.FirstAccountIsAdmin ?? true),
            RemainingUsernameChanges = 10,
        };
        player.DisplayName = player.Username;

        db.Players.Add(player);
        await db.SaveChangesAsync(cancellationToken);

        db.Progressions.Add(new Progression { PlayerId = player.Id });
        await db.SaveChangesAsync(cancellationToken);

        return player;
    }

    /// <summary>
    /// Picks a username. A requested name wins when it is free and shaped like a real one; the
    /// generated fallback is deterministic per device so a player keeps the same name across
    /// reinstalls instead of accumulating a new one on every launch.
    /// </summary>
    public static async Task<string> AllocateUsernameAsync(
        RecEmuDb db, string? requestedName, CancellationToken cancellationToken)
    {
        if (IsPlausible(requestedName) && !await db.Players.AnyAsync(p => p.Username == requestedName, cancellationToken))
            return requestedName!;

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var candidate = $"{Adjectives[Random.Shared.Next(Adjectives.Length)]}{Nouns[Random.Shared.Next(Nouns.Length)]}{Random.Shared.Next(100, 1000)}";
            if (!await db.Players.AnyAsync(p => p.Username == candidate, cancellationToken)) return candidate;
        }

        return $"Player_{Guid.NewGuid().ToString("N")[..6]}";
    }

    public static bool IsPlausible(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length is >= 3 and <= 20
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')
           && !char.IsAsciiDigit(name[0]);
}
