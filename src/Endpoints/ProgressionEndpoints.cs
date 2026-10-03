using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Progression: level, experience, quests and the weekly challenge.
///
/// Experience is derived from stored progress rather than computed from the client's reported state.
/// The client sends totals, and accepting them verbatim would let a patched client set its own level
/// — which matters more here than on a server with verified identity, because patching the client is
/// the normal way to reach this server at all. What the client does get is the authority to spend
/// earned experience on quest completion, which is where the risk actually is acceptable.
/// </summary>
public static class ProgressionEndpoints
{
    /// <summary>Experience needed for each level. A flat curve, because the real curve is not knowable.</summary>
    private static long ExperienceForLevel(long level) => level * level * 100;

    public static void Map(WebApplication app)
    {
        app.MapGet("/account/me/progression", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var progression = await LoadAsync(db, caller.Id, ct);
            return Results.Json(Payload(progression), Json.Options);
        });

        app.MapGet("/account/{accountId:int}/progression", async (int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var progression = await LoadAsync(db, accountId, ct);
            return Results.Json(Payload(progression), Json.Options);
        });

        // Bare integer: the level indicator reads a primitive, and an object here renders every
        // player as level zero.
        app.MapGet("/account/me/level", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
            Bare.Long((await LoadAsync(db, (await current.RequireAsync(ct)).Id, ct)).Level));

        app.MapPost("/account/me/progression/experience", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var amount = await http.ReadLongAsync("amount", "experience") ?? 0;
            if (amount <= 0) return Results.BadRequest();

            var progression = await LoadTrackedAsync(db, caller.Id, ct);
            progression.Experience += amount;
            ReLevel(progression);
            await db.SaveChangesAsync(ct);

            return Results.Json(Payload(progression), Json.Options);
        });

        app.MapMethods("/account/me/progression/quests", ["GET", "PUT", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            if (http.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var progression = await LoadAsync(db, caller.Id, ct);
                return Results.Json(Obj.Create(
                    ("QuestProgress", progression.QuestProgressJson),
                    ("WeeklyChallengePoints", progression.WeeklyChallengePoints)), Json.Options);
            }

            var questId = await http.ReadStringAsync("questId");
            if (string.IsNullOrWhiteSpace(questId)) return Results.BadRequest();

            var tracked = await LoadTrackedAsync(db, caller.Id, ct);
            var quests = Infrastructure.Json.Read<Dictionary<string, object>>(tracked.QuestProgressJson)
                         ?? new Dictionary<string, object>(StringComparer.Ordinal);
            quests[questId!] = await http.ReadIntAsync("progress") ?? 0;
            tracked.QuestProgressJson = Infrastructure.Json.Write(quests);
            await db.SaveChangesAsync(ct);

            return Bare.Ok();
        });

        app.MapGet("/account/me/weeklychallenge", async (CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var progression = await LoadAsync(db, caller.Id, ct);

            return Results.Json(Obj.Create(
                ("AccountId", caller.Id),
                ("Points", progression.WeeklyChallengePoints),
                ("ResetsAt", Contracts.Wire.Iso(NextWeekUtc()))), Json.Options);
        });

        app.MapGet("/api/quests/v1/weekly", () => Results.Json(Obj.Create(
            ("WeeklyChallengePoints", 0),
            ("RewardRecCurrency", 0),
            ("ResetsAt", Contracts.Wire.Iso(NextWeekUtc()))), Json.Options));
    }

    private static async Task<Progression> LoadAsync(RecEmuDb db, int playerId, CancellationToken cancellationToken)
        => await db.Progressions.AsNoTracking()
               .FirstOrDefaultAsync(p => p.PlayerId == playerId, cancellationToken)
           ?? new Progression { PlayerId = playerId, Level = 1 };

    private static async Task<Progression> LoadTrackedAsync(RecEmuDb db, int playerId, CancellationToken cancellationToken)
    {
        var row = await db.Progressions.FirstOrDefaultAsync(p => p.PlayerId == playerId, cancellationToken);
        if (row is not null) return row;

        row = new Progression { PlayerId = playerId, Level = 1 };
        db.Progressions.Add(row);
        return row;
    }

    /// <summary>
    /// Recomputes the level from total experience.
    ///
    /// Level is derived rather than incremented so the level can never drift out of step with
    /// experience — an increment-per-award scheme lets a client that awards twice skip a level
    /// permanently, and the level gate on rooms then excludes a player who should have access.
    /// </summary>
    private static void ReLevel(Progression progression)
    {
        var level = 1L;
        while (level < 1000 && progression.Experience >= ExperienceForLevel(level + 1)) level++;
        progression.Level = level;
    }

    private static DateTime NextWeekUtc()
    {
        var today = DateTime.UtcNow.Date;
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(daysUntilMonday).AddDays(7);
    }

    private static Dictionary<string, object?> Payload(Progression progression) => Obj.Create(
        ("AccountId", progression.PlayerId),
        ("Level", progression.Level),
        ("Experience", progression.Experience),
        ("ExperienceToNextLevel", ExperienceForLevel(progression.Level + 1) - progression.Experience),
        ("QuestProgress", progression.QuestProgressJson),
        ("WeeklyChallengePoints", progression.WeeklyChallengePoints));
}