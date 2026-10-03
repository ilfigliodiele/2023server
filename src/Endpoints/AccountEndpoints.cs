using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// The account surface: profile reads and writes, contact lookup, privacy settings, and the
/// version gate.
///
/// Two rules run through all of it. Field names come in aliases because the client has changed
/// them across builds and a wrong guess is not detectable — e.g. account/search is sent with
/// "name" and reading only "query" returns an empty list forever. And several responses are bare
/// scalars, which are wrapped in <see cref="Bare"/> rather than returned as objects.
/// </summary>
public static class AccountEndpoints
{
    public static void Map(WebApplication app)
    {
        MapMe(app);
        MapLookup(app);
        MapPrivacy(app);
        MapContactPrefs(app);
        MapVersionGate(app);
        MapMisc(app);
    }

    private static void MapMe(WebApplication app)
    {
        app.MapMethods("/account/me", ["GET"], async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            return player is null ? Results.Unauthorized() : Results.Json(Wire.SelfAccount(player), Json.Options);
        });

        // Deleting an account from the client is a real DELETE, and only registering GET makes
        // it 405 — the client then logs "failed to delete local account" and nothing happens.
        app.MapMethods("/account/me", ["DELETE"], async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Unauthorized();

            var player = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            player.IsDeleted = true;
            player.Username = $"deleted_{player.Id}";
            player.DisplayName = string.Empty;
            player.Email = string.Empty;
            player.Phone = string.Empty;
            player.Birthday = null;
            player.PasswordHash = string.Empty;
            player.HasPassword = false;
            player.DeviceId = string.Empty;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/api/accounts/v1/me", async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            return player is null ? Results.Unauthorized() : Results.Json(Wire.SelfAccount(player), Json.Options);
        });

        // The client builds these as "account/me/" + a suffix, so each concrete path is its own
        // route. They are all PUT except confirmphone, and every one of them returns status only:
        // the helper discards the body.
        MapFormWrite(app, "/account/me/username", "username", async (player, value, db, ct) =>
        {
            var name = value.Trim();
            if (!Accounts.IsPlausible(name)) return Bare.Ok();
            var taken = await db.Players.AnyAsync(p => p.Username == name && p.Id != player.Id, ct);
            if (taken) return Results.StatusCode(StatusCodes.Status409Conflict);
            player.Username = name;
            if (player.RemainingUsernameChanges > 0) player.RemainingUsernameChanges--;
            return Bare.Ok();
        });

        MapFormWrite(app, "/account/me/displayname", "displayname", async (player, value, db, ct) =>
        {
            player.DisplayName = value.Trim()[..Math.Min(value.Trim().Length, 50)];
            return Bare.Ok();
        });

        MapFormWrite(app, "/account/me/emoji", "emoji", async (player, value, db, ct) =>
        {
            player.DisplayEmoji = value.Trim();
            return Bare.Ok();
        });

        MapFormWrite(app, "/account/me/bio", "bio", async (player, value, db, ct) =>
        {
            player.Bio = value;
            return Bare.Ok();
        });

        MapFormWrite(app, "/account/me/personalpronouns", "personalpronouns", async (player, value, db, ct) =>
        {
            if (int.TryParse(value, out var pronouns)) player.PersonalPronouns = pronouns;
            return Bare.Ok();
        });

        MapFormWrite(app, "/account/me/identityflags", "identityflags", async (player, value, db, ct) =>
        {
            if (int.TryParse(value, out var flags)) player.IdentityFlags = flags;
            return Bare.Ok();
        });

        MapFormWrite(app, "/account/me/bannerimage", "bannerimage", async (player, value, db, ct) =>
        {
            player.BannerImage = value;
            return Bare.Ok();
        });

        MapFormWrite(app, "/account/me/profileimage", "profileimage", async (player, value, db, ct) =>
        {
            player.ProfileImage = value;
            return Bare.Ok();
        });

        // Birthday is a PUT on this build; only registering POST makes it 405, which breaks the
        // junior/age-gate prompt outright.
        app.MapMethods("/account/me/birthday", ["PUT", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);
            var raw = await http.ReadStringAsync("birthday", "date");
            if (DateTime.TryParse(raw, out var birthday)) player.Birthday = birthday.Date;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapMethods("/account/me/email", ["POST", "PUT"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);
            player.Email = (await http.ReadStringAsync("email")) ?? string.Empty;
            player.IsEmailConfirmed = false;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapMethods("/account/me/phone", ["POST", "PUT"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);
            player.Phone = (await http.ReadStringAsync("phone")) ?? string.Empty;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapPost("/account/me/confirmphone", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await current.RequireAsync(ct);
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapPost("/account/me/changepassword", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Unauthorized();

            var oldPassword = await http.ReadStringAsync("oldPassword");
            var newPassword = await http.ReadStringAsync("newPassword");
            if (!PasswordHasher.IsAcceptable(newPassword)) return Results.BadRequest();

            var player = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            if (player.HasPassword && !PasswordHasher.Verify(player.PasswordHash, oldPassword))
                return Results.BadRequest();

            player.PasswordHash = PasswordHasher.Hash(newPassword!);
            player.HasPassword = true;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/account/me/haspassword", async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            // A bare boolean. The client's typed reader rejects an array or an object here.
            return Bare.Bool(player?.HasPassword ?? false);
        });

        app.MapGet("/account/isactivecreator/me", async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            return Bare.Bool(player?.IsActiveCreator ?? false);
        });

        app.MapGet("/account/me/availableUsernameChanges", async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            return Bare.Int(player?.RemainingUsernameChanges ?? 0);
        });

        app.MapPost("/account/recoverpassword", () => Bare.Ok());

        app.MapGet("/parentalcontrol/me", async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            if (player is null) return Results.Unauthorized();
            return Results.Json(Wire.ParentalControl(player), Json.Options);
        });

        app.MapPut("/parentalcontrol/me", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Unauthorized();
            var player = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            player.DisallowInAppPurchases = await http.ReadBoolAsync("disallowInAppPurchases") ?? player.DisallowInAppPurchases;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapLookup(WebApplication app)
    {
        // The id overload switches verb at runtime: GET while the id count is under 100, POST
        // above it. Registering only one silently breaks the other.
        var bulk = async (HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var ids = (await http.ReadValuesAsync("id", "ids", "accountId"))
                .Select(v => int.TryParse(v, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (ids.Count == 0) return Results.Json(new List<object?>(), Json.Options);

            var players = await db.Players.AsNoTracking().Where(p => ids.Contains(p.Id) && !p.IsDeleted).ToListAsync(ct);
            var order = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            return Results.Json(players.OrderBy(p => order.GetValueOrDefault(p.Id, int.MaxValue)).Select(Wire.Account).ToList(), Json.Options);
        };
        app.MapMethods("/account/bulk", ["GET", "POST"], bulk);

        // Contact import: repeated email/phone fields, response carries ContactDetail first.
        app.MapPost("/account/bulk/email", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
            await ContactLookupAsync(http, db, "email", p => p.Email, ct));
        app.MapPost("/account/bulk/phone", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
            await ContactLookupAsync(http, db, "phone", p => p.Phone, ct));

        // The client sends the search text as "name"; binding only "query" makes player search
        // return nothing at all, with no error.
        app.MapGet("/account/search", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var term = (await http.ReadStringAsync("name", "query"))?.Trim() ?? string.Empty;
            if (term.Length < 2) return Results.Json(new List<object?>(), Json.Options);

            var players = await db.Players
                .AsNoTracking()
                .Where(p => !p.IsDeleted && (p.Username.Contains(term) || p.DisplayName.Contains(term)))
                .OrderBy(p => p.Username)
                .Take(50)
                .ToListAsync(ct);

            return Results.Json(players.Select(Wire.Account).ToList(), Json.Options);
        });

        app.MapGet("/account/{id:int}/bio", async (int id, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
            return player is null ? Results.NotFound() : Results.Json(Wire.Bio(player.Id, player.Bio), Json.Options);
        });

        app.MapGet("/accounts/{id:int}", async (int id, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
            return player is null ? Results.NotFound() : Results.Json(Wire.Account(player), Json.Options);
        });

        app.MapGet("/api/accounts/v1/{id:int}", async (int id, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
            return player is null ? Results.NotFound() : Results.Json(Wire.Account(player), Json.Options);
        });

        // A bare JSON string. The neighbouring route returns an object with PlatformId in it,
        // which this reader cannot parse at all.
        app.MapGet("/platformid/{id:int}", async (int id, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
            return Bare.Text(player?.PlatformId ?? string.Empty);
        });

        app.MapGet("/account/v1/{id:int}/platformid", async (int id, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
            return Bare.Text(player?.PlatformId ?? string.Empty);
        });

        // The {category} segment is an enum member name, not a number, and the names are not
        // recoverable — so it is treated as opaque and never constrained.
        app.MapGet("/accounts/{id:int}/receives/{category}", () => Bare.Bool(true));

        app.MapGet("/namegen/options", () => Results.Json(Obj.Create(
            ("Nouns", Accounts.NameNouns.ToList()),
            ("Adjectives", Accounts.NameAdjectives.ToList())), Json.Options));

        app.MapGet("/account/forplatformid", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var platform = await http.ReadIntAsync("platform") ?? 0;
            var platformId = (await http.ReadStringAsync("platformId", "platform_id")) ?? string.Empty;
            var player = await db.Players
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Platform == platform && p.PlatformId == platformId && !p.IsDeleted, ct);
            return Results.Json(new List<object?> { player is null ? null : Wire.Account(player) }, Json.Options);
        });

        app.MapGet("/account/me/forplatformid", async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            return Results.Json(new List<object?> { player is null ? null : Wire.Account(player) }, Json.Options);
        });
    }

    private static void MapPrivacy(WebApplication app)
    {
        app.MapGet("/accountprivacysettings/{id:int}", async (int id, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
            return player is null ? Results.NotFound() : Results.Json(Wire.PrivacySettings(player), Json.Options);
        });

        app.MapGet("/accountprivacysettings/me", async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            return player is null ? Results.Unauthorized() : Results.Json(Wire.PrivacySettings(player), Json.Options);
        });

        app.MapPut("/accountprivacysettings/recenthistoryvisibility", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Unauthorized();
            var player = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            player.IsRecentHistoryVisible = await http.ReadBoolAsync("isRecentHistoryVisible") ?? player.IsRecentHistoryVisible;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/accountprivacysettings/recenthistoryvisibility/me", async (CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var player = await current.GetAsync(ct);
            return Bare.Bool(player?.IsRecentHistoryVisible ?? false);
        });
    }

    private static void MapContactPrefs(WebApplication app)
    {
        // PUT, not GET: the client calls this straight after setting an email address and a
        // GET-only route answers 405.
        app.MapMethods("/api/players/v4/current/contact", ["PUT", "GET", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Unauthorized();

            var player = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            var email = await http.ReadStringAsync("email");
            if (!string.IsNullOrWhiteSpace(email)) player.Email = email!;
            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });
    }

    private static void MapVersionGate(WebApplication app)
    {
        // Always OK: this server exists precisely to serve the build it is configured for, and a
        // gate that ever returned "update required" would lock every player out of the game.
        app.MapGet("/api/versioncheck/v4", () => Results.Json(Obj.Create(
            ("VersionStatus", Enums.VersionStatus.Ok),
            ("UpdateNotificationStage", 0)), Json.Options));

        app.MapGet("/api/versioncheck", () => Results.Json(Obj.Create(
            ("VersionStatus", Enums.VersionStatus.Ok),
            ("UpdateNotificationStage", 0)), Json.Options));
    }

    private static void MapMisc(WebApplication app)
    {
        app.MapGet("/api/sanitize/v1/config", () => Results.Json(new Dictionary<string, object?> { ["enabled"] = false }, Json.Options));

        app.MapPost("/api/sanitize/v1", async (HttpContext http, CancellationToken ct) =>
        {
            var text = (await http.ReadStringAsync("text", "message", "string", "value")) ?? string.Empty;
            return Bare.Text(text);
        });

        app.MapPost("/api/sanitize/v1/isPure", async (HttpContext http, CancellationToken ct) =>
        {
            var text = (await http.ReadStringAsync("text", "message", "string", "value")) ?? string.Empty;
            var isPure = !ContainsBlockedWord(text);
            return Results.Json(Obj.Create(("IsPure", isPure)), Json.Options);
        });

        app.MapGet("/api/namegen/me", () => Bare.Text(""));
    }

    private static readonly string[] BlockedWords =
    {
        "fuck", "shit", "bitch", "cunt", "nigger", "faggot", "rape", "kys",
    };

    private static bool ContainsBlockedWord(string text)
        => BlockedWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static async Task<IResult> ContactLookupAsync(
        HttpContext http, RecEmuDb db, string field, Func<Player, string> selector, CancellationToken ct)
    {
        var values = (await http.ReadValuesAsync(field))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (values.Count == 0) return Results.Json(new List<object?>(), Json.Options);

        var players = await db.Players.AsNoTracking().Where(p => !p.IsDeleted).ToListAsync(ct);
        var rows = players
            .Where(p => values.Contains(selector(p), StringComparer.OrdinalIgnoreCase))
            .Select(p =>
            {
                // ContactDetail leads the row; the account keys follow. The reader walks them in
                // order, so the matched value has to stay first.
                var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ContactDetail"] = selector(p) };
                foreach (var (key, value) in Wire.Account(p)) row[key] = value;
                return row;
            })
            .ToList();

        return Results.Json(rows, Json.Options);
    }

    private static void MapFormWrite(
        WebApplication app, string path, string field,
        Func<Player, string, RecEmuDb, CancellationToken, Task<IResult>> handler)
    {
        app.MapMethods(path, ["PUT", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Unauthorized();

            var value = (await http.ReadStringAsync(field, "value")) ?? string.Empty;
            var player = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            var result = await handler(player, value, db, ct);
            await db.SaveChangesAsync(ct);
            return result;
        });
    }
}
