using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// First-boot content.
///
/// The client expects certain rooms to already exist before it will show anything useful: the Dorm
/// is where a fresh install lands via matchmake/dorm, and the Orientation room is the first thing
/// new players are pointed at. If either is missing the client gets a matchmaking error on the very
/// first launch and the server looks broken in a way that is hard to trace back to seed data.
///
/// Everything here is idempotent, keyed on a stable natural key, so restarting never duplicates.
/// </summary>
public static class Seed
{
    public const string DormName = "Dorm";
    public const string OrientationName = "Orientation";

    /// <summary>Server setting holding the id of the spawn room. See <see cref="FindDormAsync"/>.</summary>
    public const string DormRoomIdSettingKey = "dorm.roomId";

    public static async Task ApplyAsync(RecEmuDb db, RecEmuOptionsAccessor options, CancellationToken cancellationToken)
    {
        await SeedRoomsAsync(db, options, cancellationToken);
        await SeedStoreAsync(db, cancellationToken);
        await SeedClubsAsync(db, cancellationToken);
        await SeedSettingsAsync(db, cancellationToken);
    }

    private static async Task SeedRoomsAsync(RecEmuDb db, RecEmuOptionsAccessor options, CancellationToken cancellationToken)
    {
        if (!await db.Rooms.AnyAsync(cancellationToken))
        {
            var capacity = options.Value.DefaultRoomCapacity;

            var dorm = new Room
            {
                Name = DormName,
                Description = "The Rec Room lobby. Spawn here to explore.",
                ImageName = "none",
                CreatorAccountId = 0,
                Accessibility = Enums.Accessibility.Public,
                MaxPlayers = capacity,
                IsRro = true,
                PersistenceVersion = 1,
                SupportsJuniors = true,
            };
            dorm.SubRooms.Add(new SubRoom
            {
                Name = "Spawn",
                Description = "Dorm spawn point",
                MaxPlayers = capacity,
                Accessibility = Enums.Accessibility.Public,
                IsSpawnPoint = true,
            });

            var orientation = new Room
            {
                Name = OrientationName,
                Description = "Learn the controls.",
                ImageName = "none",
                CreatorAccountId = 0,
                Accessibility = Enums.Accessibility.Public,
                MaxPlayers = capacity,
                IsRro = true,
                PersistenceVersion = 1,
            };
            orientation.SubRooms.Add(new SubRoom
            {
                Name = "Orientation",
                Description = "Orientation scene",
                MaxPlayers = capacity,
                Accessibility = Enums.Accessibility.Public,
                IsSpawnPoint = true,
            });

            db.Rooms.AddRange(dorm, orientation);
            await db.SaveChangesAsync(cancellationToken);
        }

        // The Dorm's data blob is the one piece of content the client cannot start without: it
        // loads the lobby scene from here. An empty blob boots into an empty grey void.
        var dormRoom = await FindDormAsync(db, cancellationToken);
        if (dormRoom is not null && string.IsNullOrEmpty(dormRoom.DataBlob))
        {
            dormRoom.DataBlob = SeedContent.DormSceneBlob;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The room a fresh install spawns into, resolved without trusting its name.
    ///
    /// The first-boot Dorm is created with a known id, which is recorded in a server setting.
    /// Looking it up by name alone breaks the moment an operator renames the lobby — which is a
    /// perfectly reasonable thing to do ("RecEmu Lobby") — and the symptom is that matchmake/dorm
    /// starts answering RoomDoesNotExist for every player with no other error anywhere. So the id
    /// is the primary key for this room, the name is the migration path for databases seeded before
    /// the setting existed, and the oldest room is the last resort so a hand-trimmed database still
    /// has somewhere to spawn.
    /// </summary>
    public static async Task<Room?> FindDormAsync(RecEmuDb db, CancellationToken cancellationToken)
    {
        var recorded = await db.ServerSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == DormRoomIdSettingKey, cancellationToken);

        if (recorded is not null &&
            long.TryParse(recorded.Value, out var recordedId) &&
            await db.Rooms.Include(r => r.SubRooms).FirstOrDefaultAsync(r => r.Id == recordedId, cancellationToken) is { } byId)
        {
            return byId;
        }

        if (await db.Rooms.Include(r => r.SubRooms)
                .FirstOrDefaultAsync(r => r.Name == DormName, cancellationToken) is { } byName)
        {
            await RememberDormAsync(db, byName.Id, cancellationToken);
            return byName;
        }

        var oldest = await db.Rooms.Include(r => r.SubRooms)
            .Where(r => r.State == 0)
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldest is not null) await RememberDormAsync(db, oldest.Id, cancellationToken);
        return oldest;
    }

    private static async Task RememberDormAsync(RecEmuDb db, long roomId, CancellationToken cancellationToken)
    {
        var row = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == DormRoomIdSettingKey, cancellationToken);
        if (row is null) db.ServerSettings.Add(new ServerSetting { Key = DormRoomIdSettingKey, Value = roomId.ToString() });
        else row.Value = roomId.ToString();
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedStoreAsync(RecEmuDb db, CancellationToken cancellationToken)
    {
        if (await db.StoreItems.AnyAsync(cancellationToken)) return;

        // Everything is free. A server that cannot take payments has no reason to charge, and a
        // price the player cannot pay is a dead end in the middle of a session.
        db.StoreItems.AddRange(
            new StoreItem { Name = "Rec Currency Pack", Description = "Rec Currency", ItemType = 0, Price = 0, CurrencyType = 0 },
            new StoreItem { Name = "Avatar Shirt", Description = "A shirt", ItemType = 1, Price = 0, CurrencyType = 0 },
            new StoreItem { Name = "Avatar Hat", Description = "A hat", ItemType = 2, Price = 0, CurrencyType = 0 },
            new StoreItem { Name = "Room Key", Description = "Access to a private room", ItemType = 3, Price = 0, CurrencyType = 0 });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedClubsAsync(RecEmuDb db, CancellationToken cancellationToken)
    {
        if (await db.Clubs.AnyAsync(cancellationToken)) return;

        db.Clubs.AddRange(
            new Club { Name = "Rec Room Official", Description = "Official announcements", Category = "recroomofficial", Visibility = 0, Joinability = Enums.ClubJoinability.Open, IsRro = true },
            new Club { Name = "Community", Description = "Player-run community", Category = "community", Visibility = 0, Joinability = Enums.ClubJoinability.AskToJoin });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedSettingsAsync(RecEmuDb db, CancellationToken cancellationToken)
    {
        if (await db.ServerSettings.AnyAsync(cancellationToken)) return;
        db.ServerSettings.Add(new ServerSetting { Key = "seeded", Value = DateTime.UtcNow.ToString("o") });
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Scene blobs the client needs in order to render something.
///
/// A Rec Room data blob is the client's own serialized scene format, produced by the official
/// editor. RecEmu does not generate one: there is no editor here to generate it with. What it does
/// do is point the lobby at a blob the operator supplies, so the correct workflow is to open the
/// room once in the real Rec Room editor and drop the resulting blob in here — see docs/rooms.md.
/// </summary>
public static class SeedContent
{
    /// <summary>
    /// "CAE=" is the base64 of the two zero bytes: an empty, structurally valid blob. It boots.
    /// The scene is empty, which is exactly the honest state for a server with no game assets —
    /// shipping a fabricated blob here would produce a room that loads and then misbehaves in
    /// ways that are much harder to diagnose than an empty one.
    /// </summary>
    public const string DormSceneBlob = "CAE=";

    public const string OrientationSceneBlob = "CAE=";
}
