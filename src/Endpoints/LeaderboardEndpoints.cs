using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Leaderboards and the stat routes they read.
///
/// Ordering is done after materialising rather than in SQL. The top-N query is cheap, but the row's
/// rank is not: "is this player in the top N" requires knowing how many players have a higher value,
/// which is a second aggregate. Ranking in memory keeps the two consistent with each other — a
/// player listed at rank 3 is in the same list at position 3, rather than being absent from a
/// top-100 response because the rank query and the page query disagreed about ties.
/// </summary>
public static class LeaderboardEndpoints
{
    public static void Map(WebApplication app)
    {
        MapBoards(app);
        MapStats(app);
    }

    private static void MapBoards(WebApplication app)
    {
        app.MapGet("/leaderboards", () => Results.Json(Obj.Create(
            ("Leaderboards", new List<object?>
            {
                Obj.Create(("StatName", "times.played"), ("DisplayName", "Times Played")),
                Obj.Create(("StatName", "rooms.visited"), ("DisplayName", "Rooms Visited")),
                Obj.Create(("StatName", "currency.recurrency"), ("DisplayName", "Rec Currency Earned")),
                Obj.Create(("StatName", "rooms.built"), ("DisplayName", "Rooms Built")),
            })), Json.Options));

        app.MapGet("/leaderboards/{statName}/me", async (
            string statName, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var rows = await db.Stats.AsNoTracking().Where(s => s.StatName == statName).ToListAsync(ct);

            var ordered = rows.OrderByDescending(r => r.Value).ThenBy(r => r.PlayerId).ToList();
            var mine = ordered.FindIndex(r => r.PlayerId == caller.Id);

            return Results.Json(Obj.Create(
                ("Value", mine < 0 ? 0 : ordered[mine].Value),
                ("Rank", mine < 0 ? 0 : mine + 1),
                ("TotalPlayers", ordered.Count)), Json.Options);
        });

        app.MapGet("/leaderboards/{statName}/top", async (
            string statName, HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 100, 1, 500);
            return Results.Json(await TopAsync(db, statName, take, ct), Json.Options);
        });

        // The client's leaderboard screen asks for "top N around me", so the caller's own rank has to
        // be in the response or the highlighted row cannot be drawn.
        app.MapGet("/leaderboards/{statName}", async (
            string statName, HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 25, 1, 200);
            var top = await TopAsync(db, statName, take, ct);

            var caller = await current.GetAsync(ct);
            if (caller is null) return Results.Json(top, Json.Options);

            var all = await db.Stats.AsNoTracking().Where(s => s.StatName == statName).ToListAsync(ct);
            var rank = all.Count(r => r.Value > 0) >= 0
                ? all.OrderByDescending(r => r.Value).ThenBy(r => r.PlayerId).ToList()
                    .FindIndex(r => r.PlayerId == caller.Id) + 1
                : 0;

            return Results.Json(Obj.Paged(top, Math.Max(rank, all.Count)), Json.Options);
        });
    }

    private static async Task<List<Dictionary<string, object?>>> TopAsync(
        RecEmuDb db, string statName, int take, CancellationToken ct)
    {
        var rows = await db.Stats.AsNoTracking()
            .Where(s => s.StatName == statName && s.Value > 0)
            .OrderByDescending(s => s.Value)
            .ThenBy(s => s.PlayerId)
            .Take(take)
            .ToListAsync(ct);

        var players = await db.Players.AsNoTracking()
            .Where(p => rows.Select(r => r.PlayerId).Contains(p.Id) && !p.IsDeleted)
            .ToListAsync(ct);
        var byId = players.ToDictionary(p => p.Id);

        var result = new List<Dictionary<string, object?>>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var player = byId.GetValueOrDefault(row.PlayerId);
            result.Add(Obj.Create(
                ("Rank", index + 1),
                ("Value", row.Value),
                ("AccountId", row.PlayerId),
                ("Player", player is null ? null : Wire.Account(player)),
                ("UserName", player?.Username ?? string.Empty),
                ("DisplayName", player?.DisplayName ?? string.Empty),
                ("ProfileImage", player?.ProfileImage ?? "none")));
        }

        return result;
    }

    private static void MapStats(WebApplication app)
    {
        // Bare integers: the stats screen reads each channel as a primitive, and a wrapped object
        // makes every stat render as zero without an error.
        app.MapGet("/account/{accountId:int}/stats/{statName}", async (
            int accountId, string statName, RecEmuDb db, CancellationToken ct) =>
        {
            var value = await db.Stats.AsNoTracking()
                .Where(s => s.StatName == statName && s.PlayerId == accountId)
                .Select(s => (long?)s.Value)
                .FirstOrDefaultAsync(ct) ?? 0;
            return Bare.Long(value);
        });

        // "me" is not an int, so the accountId route above cannot serve it. Without this the write route
        // below is the only match and the client gets a 405 on a plain read of its own stat.
        app.MapGet("/account/me/stats/{statName}", async (
            string statName, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var value = await db.Stats.AsNoTracking()
                .Where(s => s.StatName == statName && s.PlayerId == caller.Id)
                .Select(s => (long?)s.Value)
                .FirstOrDefaultAsync(ct) ?? 0;
            return Bare.Long(value);
        });

        app.MapMethods("/account/me/stats/{statName}", ["PUT", "POST"], async (
            HttpContext http, string statName, CurrentPlayerAccessor current, StatRecordService stats,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var value = await http.ReadLongAsync("value") ?? 0;
            var row = await stats.SetAsync(statName, caller.Id, value, ct);
            return Bare.Long(row.Value);
        });

        app.MapMethods("/account/me/stats/{statName}/increment", ["PUT", "POST"], async (
            HttpContext http, string statName, CurrentPlayerAccessor current, StatRecordService stats,
            CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var amount = await http.ReadLongAsync("amount", "value") ?? 0;
            var row = await stats.RecordAsync(statName, caller.Id, amount, ct);
            return Bare.Long(row.Value);
        });

        app.MapGet("/account/me/stats", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var rows = await db.Stats.AsNoTracking()
                .Where(s => s.PlayerId == caller.Id)
                .ToListAsync(ct);

            return Results.Json(rows.ToDictionary(
                r => r.StatName,
                r => r.Value,
                StringComparer.Ordinal), Json.Options);
        });
    }
}