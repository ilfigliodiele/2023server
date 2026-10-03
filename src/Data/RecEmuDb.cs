using Microsoft.EntityFrameworkCore;

namespace RecEmu.Server.Data;

public sealed class RecEmuDb(DbContextOptions<RecEmuDb> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LoginToken> LoginTokens => Set<LoginToken>();
    public DbSet<DeviceAuthorization> DeviceAuthorizations => Set<DeviceAuthorization>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<SubRoom> SubRooms => Set<SubRoom>();
    public DbSet<SubRoomDataSave> SubRoomDataSaves => Set<SubRoomDataSave>();
    public DbSet<RoomRoleAssignment> RoomRoles => Set<RoomRoleAssignment>();
    public DbSet<RoomBan> RoomBans => Set<RoomBan>();
    public DbSet<PlayerRoomData> PlayerRoomData => Set<PlayerRoomData>();
    public DbSet<RoomInteraction> RoomInteractions => Set<RoomInteraction>();
    public DbSet<RoomInstance> RoomInstances => Set<RoomInstance>();
    public DbSet<Relationship> Relationships => Set<Relationship>();
    public DbSet<FriendRequest> FriendRequests => Set<FriendRequest>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<ClubMember> ClubMembers => Set<ClubMember>();
    public DbSet<Thread> Threads => Set<Thread>();
    public DbSet<ThreadMember> ThreadMembers => Set<ThreadMember>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<RoomComment> RoomComments => Set<RoomComment>();
    public DbSet<StatRecord> Stats => Set<StatRecord>();
    public DbSet<StoreItem> StoreItems => Set<StoreItem>();
    public DbSet<InventoryEntry> Inventory => Set<InventoryEntry>();
    public DbSet<PlayerSetting> PlayerSettings => Set<PlayerSetting>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<PlayerWarning> PlayerWarnings => Set<PlayerWarning>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<Progression> Progressions => Set<Progression>();
    public DbSet<Creation> Creations => Set<Creation>();
    public DbSet<VideoRecord> Videos => Set<VideoRecord>();
    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Player>(entity =>
        {
            entity.HasIndex(p => p.DeviceId);
            entity.HasIndex(p => p.Username);
            entity.HasIndex(p => p.PlatformId);
        });

        model.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(t => t.TokenHash);
            entity.HasOne(t => t.Player).WithMany(p => p.RefreshTokens).HasForeignKey(t => t.PlayerId);
        });

        model.Entity<LoginToken>(entity => entity.HasIndex(t => t.Token));
        model.Entity<DeviceAuthorization>(entity => entity.HasKey(d => d.DeviceCode));

        model.Entity<Room>(entity =>
        {
            entity.HasIndex(r => r.CreatorAccountId);
            entity.HasMany(r => r.SubRooms).WithOne(s => s.Room!).HasForeignKey(s => s.RoomId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(r => r.Roles).WithOne().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(r => r.Bans).WithOne().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SubRoom>().HasIndex(s => s.RoomId);
        model.Entity<SubRoomDataSave>().HasIndex(s => s.SubRoomId);
        model.Entity<RoomRoleAssignment>().HasIndex(r => new { r.RoomId, r.AccountId });
        model.Entity<RoomBan>().HasIndex(b => new { b.RoomId, b.BannedPlayerId });
        model.Entity<PlayerRoomData>().HasIndex(d => new { d.PlayerId, d.RoomId });
        model.Entity<RoomInteraction>().HasIndex(i => new { i.PlayerId, i.RoomId });
        model.Entity<RoomInstance>().HasIndex(i => i.RoomId);

        model.Entity<Relationship>().HasIndex(r => new { r.AccountId, r.TargetAccountId });
        model.Entity<FriendRequest>().HasIndex(r => new { r.SenderAccountId, r.ReceiverAccountId });
        model.Entity<ClubMember>().HasIndex(m => new { m.ClubId, m.AccountId });
        model.Entity<ThreadMember>().HasIndex(m => new { m.ThreadId, m.AccountId });
        model.Entity<Thread>().HasIndex(t => t.RoomId);
        model.Entity<Thread>().HasIndex(t => t.ClubId);
        model.Entity<ChatMessage>().HasIndex(m => m.ThreadId);
        model.Entity<RoomComment>().HasIndex(c => c.RoomId);
        model.Entity<StatRecord>().HasIndex(s => new { s.StatName, s.PlayerId });
        model.Entity<StoreItem>().HasIndex(s => s.Name);
        model.Entity<InventoryEntry>().HasIndex(i => new { i.PlayerId, i.Category, i.ItemId });
        model.Entity<PlayerSetting>().HasIndex(s => new { s.PlayerId, s.Key });
        model.Entity<Report>().HasIndex(r => r.ReportedPlayerId);
        model.Entity<Report>().HasIndex(r => r.ReportedRoomId);
        model.Entity<PlayerWarning>().HasIndex(w => w.PlayerId);
        model.Entity<Blob>().HasIndex(b => b.Name);
        model.Entity<Announcement>().HasIndex(a => a.ClubId);
        model.Entity<Progression>().HasIndex(p => p.PlayerId);
        model.Entity<Creation>().HasIndex(c => new { c.Type, c.CreatorAccountId });
        model.Entity<VideoRecord>().HasIndex(v => v.CreatorAccountId);
        model.Entity<ServerSetting>().HasKey(s => s.Key);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        TouchModified();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        TouchModified();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Stamps UpdatedAt on entities that carry it. Doing it here rather than in every handler is
    /// what keeps room metadata from looking freshly saved while its own timestamp says otherwise.
    /// </summary>
    private void TouchModified()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified) continue;
            var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UpdatedAt");
            if (property is not null && !Equals(property.CurrentValue, now)) property.CurrentValue = now;
        }
    }
}
