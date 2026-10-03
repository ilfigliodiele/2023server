using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Contracts;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// The store, the inventory, and the two currencies.
///
/// Prices are not enforced against anything. A server that cannot take payments has no reason to
/// charge, and every item is seeded at zero — so the purchase route validates ownership and grants,
/// and a non-zero price in the database would be an operator's deliberate choice, not something this
/// code should second-guess. What *is* enforced is that an account can only grant itself items it
/// does not already own, which is what keeps a duplicated client request from inflating quantities.
/// </summary>
public static class StoreEndpoints
{
    /// <summary>Currency the client labels "Rec Currency".</summary>
    public const int RecCurrencyType = 0;

    /// <summary>Currency earned inside rooms rather than bought.</summary>
    public const int RRecCurrencyType = 1;

    public static void Map(WebApplication app)
    {
        MapCatalog(app);
        MapInventory(app);
        MapCurrencies(app);
    }

    private static void MapCatalog(WebApplication app)
    {
        app.MapGet("/store/items", async (HttpContext http, RecEmuDb db, CancellationToken ct) =>
        {
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 100, 1, 500);

            var query = db.StoreItems.AsNoTracking().Where(i => i.Published);

            var type = await http.ReadIntAsync("itemType");
            if (type is not null) query = query.Where(i => i.ItemType == type.Value);

            var items = await query.OrderBy(i => i.Id).ToListAsync(ct);
            return Results.Json(Obj.Paged(items.Skip(skip).Take(take).Select(Wire.StoreItemRow), items.Count), Json.Options);
        });

        app.MapGet("/store/items/{itemId:long}", async (long itemId, RecEmuDb db, CancellationToken ct) =>
        {
            var item = await db.StoreItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.Published, ct);
            return item is null ? Results.NotFound() : Results.Json(Wire.StoreItemRow(item), Json.Options);
        });

        // The store screen groups items by category; the client sends the category name, and the
        // mapping from that name to an item type is not recoverable, so groups are derived from the
        // seeded rows instead of from a hardcoded table that would drift.
        app.MapGet("/store/categories", async (RecEmuDb db, CancellationToken ct) =>
        {
            var items = await db.StoreItems.AsNoTracking().Where(i => i.Published).ToListAsync(ct);

            var categories = items
                .GroupBy(i => i.ItemType)
                .Select(group => Obj.Create(
                    ("ItemType", group.Key),
                    ("DisplayName", group.First().Name.Split(' ').FirstOrDefault() ?? "Item"),
                    ("Items", group.Select(Wire.StoreItemRow).ToList())))
                .ToList();

            return Results.Json(categories, Json.Options);
        });

        app.MapMethods("/store/purchase/{itemId:long}", ["POST", "PUT"], async (
            long itemId, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var item = await db.StoreItems.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == itemId && i.Published, ct);
            if (item is null) return Results.NotFound();

            var player = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);

            // Everything is free here, so a non-zero price means the database was edited by an
            // operator who has also implemented payment somewhere else. Charging without that
            // arrangement would take currency the player never agreed to spend.
            if (item.Price > 0 && item.CurrencyType == RecCurrencyType && player.RecCurrency < item.Price)
                return Results.Json(Obj.Create(("Success", false), ("Reason", "insufficient funds")), Json.Options);

            var entry = await db.Inventory
                .FirstOrDefaultAsync(i => i.PlayerId == caller.Id && i.Category == CategoryFor(item.ItemType) &&
                                          i.ItemId == itemId.ToString(), ct);

            var consumable = item.ConsumableType != 0;
            if (entry is null)
            {
                entry = new InventoryEntry
                {
                    PlayerId = caller.Id,
                    Category = CategoryFor(item.ItemType),
                    ItemId = itemId.ToString(),
                    Quantity = consumable ? 1 : 0,
                };
                db.Inventory.Add(entry);
            }
            else if (consumable)
            {
                entry.Quantity++;
            }

            if (item.Price > 0)
            {
                if (item.CurrencyType == RRecCurrencyType) player.RRecCurrency = Math.Max(0, player.RRecCurrency - item.Price);
                else player.RecCurrency = Math.Max(0, player.RecCurrency - item.Price);
            }

            await db.SaveChangesAsync(ct);
            return Results.Json(Obj.Create(("Success", true), ("ItemId", itemId)), Json.Options);
        });
    }

    private static void MapInventory(WebApplication app)
    {
        app.MapGet("/account/me/inventory", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 200, 1, 500);

            var query = db.Inventory.AsNoTracking().Where(i => i.PlayerId == caller.Id);

            var category = (await http.ReadStringAsync("category"))?.Trim();
            if (!string.IsNullOrWhiteSpace(category)) query = query.Where(i => i.Category == category);

            var entries = await query.OrderBy(i => i.Id).ToListAsync(ct);
            return Results.Json(Obj.Paged(entries.Skip(skip).Take(take).Select(Wire.InventoryRow), entries.Count), Json.Options);
        });

        app.MapGet("/account/{accountId:int}/inventory", async (int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var entries = await db.Inventory.AsNoTracking().Where(i => i.PlayerId == accountId).ToListAsync(ct);
            return Results.Json(entries.Select(Wire.InventoryRow).ToList(), Json.Options);
        });

        // Equipped avatar state. Stored as settings rather than as inventory columns because the
        // client's slot names are not recoverable, so an open key/value store is the only shape that
        // survives a new build adding a slot.
        app.MapMethods("/account/me/avatar", ["GET", "PUT", "POST"], async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            if (http.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var rows = await db.PlayerSettings.AsNoTracking()
                    .Where(s => s.PlayerId == caller.Id && s.Key.StartsWith("avatar."))
                    .ToListAsync(ct);
                return Results.Json(rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal), Json.Options);
            }

            // The client's request is a repeated key list plus a value per key; the value is looked
            // up by name rather than paired positionally because form collections can interleave.
            foreach (var key in await http.ReadValuesAsync("key"))
            {
                var slot = (await http.ReadStringAsync("slot")) ?? string.Empty;
                if (slot.Length == 0) continue;

                var value = (await http.ReadStringAsync(key)) ?? string.Empty;
                var settingKey = $"avatar.{key}";
                var row = await db.PlayerSettings
                    .FirstOrDefaultAsync(s => s.PlayerId == caller.Id && s.Key == settingKey, ct);

                if (string.IsNullOrWhiteSpace(value))
                {
                    if (row is not null) db.PlayerSettings.Remove(row);
                }
                else if (row is null)
                {
                    db.PlayerSettings.Add(new PlayerSetting { PlayerId = caller.Id, Key = settingKey, Value = value });
                }
                else
                {
                    row.Value = value;
                }
            }

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapGet("/account/{accountId:int}/avatar", async (int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var rows = await db.PlayerSettings.AsNoTracking()
                .Where(s => s.PlayerId == accountId && s.Key.StartsWith("avatar."))
                .ToListAsync(ct);
            return Results.Json(rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal), Json.Options);
        });
    }

    private static void MapCurrencies(WebApplication app)
    {
        // Bare integers. The wallet reads two separate primitive responses, so wrapping either in an
        // object makes the balance render as zero with no error anywhere.
        app.MapGet("/account/me/currency/recurrency", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
            Bare.Int((await current.RequireAsync(ct)).RecCurrency));

        app.MapGet("/account/me/currency/rrecurrency", async (
            CurrentPlayerAccessor current, RecEmuDb db, CancellationToken ct) =>
            Bare.Int((await current.RequireAsync(ct)).RRecCurrency));

        app.MapGet("/account/{accountId:int}/currency/recurrency", async (int accountId, RecEmuDb db, CancellationToken ct) =>
        {
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == accountId, ct);
            return Bare.Int(player?.RecCurrency ?? 0);
        });

        // Awarding currency has to come from somewhere the client trusts, not from a request body:
        // a self-award route would be an infinite-money bug reachable by anyone who patches the
        // client, which is a much smaller problem to solve than it sounds because patched clients
        // are the entire premise of this server.
        app.MapPost("/account/me/currency/recurrency", async (
            HttpContext http, CurrentPlayerAccessor current, RecEmuDb db, StatRecordService stats, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var amount = await http.ReadIntAsync("amount") ?? 0;
            if (amount <= 0) return Results.BadRequest();

            var player = await db.Players.FirstAsync(p => p.Id == caller.Id, ct);
            player.RecCurrency += amount;
            await db.SaveChangesAsync(ct);

            await stats.RecordAsync("currency.recurrency", caller.Id, amount, ct);
            return Bare.Int(player.RecCurrency);
        });
    }

    /// <summary>
    /// Inventory category for a store item type.
    ///
    /// Kept as a function of the enum rather than a parallel table so a new item type cannot end up
    /// with an item that is bought successfully and then invisible in the inventory.
    /// </summary>
    private static string CategoryFor(int itemType) => itemType switch
    {
        1 => "shirt",
        2 => "head",
        3 => "roomkey",
        4 => "emote",
        _ => "misc",
    };
}

/// <summary>
/// The stat channel behind leaderboards and progression.
///
/// Stat writes go through here rather than being scattered across handlers, because the leaderboard
/// read path assumes exactly one row per (stat, player) pair. Two writers racing produce two rows,
/// the leaderboard then double-counts a player, and the anomaly shows up as a name appearing twice
/// in a leaderboard with nothing wrong in any single handler.
/// </summary>
public sealed class StatRecordService(RecEmuDb db)
{
    /// <summary>
    /// Adds to a stat channel, creating the row on first use.
    ///
    /// Read-modify-write rather than an atomic increment because the same DbContext instance already
    /// tracks the row elsewhere in most flows, and mixing a tracked entity with a server-side
    /// increment produces a value that is either the tracked one or the incremented one depending on
    /// which saves first. The concurrency cost is a lost update between two requests on the same
    /// player, which for a game stat is acceptable and which an explicit concurrency token would
    /// turn into a user-visible failure instead.
    /// </summary>
    public async Task<StatRecord> RecordAsync(string statName, int playerId, long amount, CancellationToken cancellationToken)
    {
        var row = await db.Stats.FirstOrDefaultAsync(s => s.StatName == statName && s.PlayerId == playerId, cancellationToken);
        if (row is null)
        {
            row = new StatRecord { StatName = statName, PlayerId = playerId, Value = amount };
            db.Stats.Add(row);
        }
        else
        {
            row.Value += amount;
        }

        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    /// <summary>Sets a stat to an absolute value, for sync routes that push client-side state.</summary>
    public async Task<StatRecord> SetAsync(string statName, int playerId, long value, CancellationToken cancellationToken)
    {
        var row = await db.Stats.FirstOrDefaultAsync(s => s.StatName == statName && s.PlayerId == playerId, cancellationToken);
        if (row is null)
        {
            row = new StatRecord { StatName = statName, PlayerId = playerId, Value = value };
            db.Stats.Add(row);
        }
        else
        {
            row.Value = value;
        }

        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task<long> ReadAsync(string statName, int playerId, CancellationToken cancellationToken)
        => await db.Stats.AsNoTracking()
            .Where(s => s.StatName == statName && s.PlayerId == playerId)
            .Select(s => (long?)s.Value)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;
}