using System.ComponentModel.DataAnnotations;

namespace RecEmu.Server.Data;

/// <summary>
/// A player account. The 2023 client has no real authentication, so the account is keyed by the
/// per-install device id the client generates; platform ids are recorded when available but never
/// trusted for identity (Steam, in particular, is deliberately not verified so emulators and
/// side-loaded clients work).
/// </summary>
public sealed class Player
{
    public int Id { get; set; }

    /// <summary>The per-install device id. Stable for one game installation.</summary>
    public string DeviceId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string ProfileImage { get; set; } = "none";
    public string BannerImage { get; set; } = "none";
    public string DisplayEmoji { get; set; } = "happy";
    public int PersonalPronouns { get; set; }
    public int IdentityFlags { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public DateTime? Birthday { get; set; }

    public string PasswordHash { get; set; } = string.Empty;
    public bool HasPassword { get; set; }

    public bool IsJunior { get; set; }
    public int JuniorState { get; set; }
    public int? ParentAccountId { get; set; }
    public bool TreatAsJunior { get; set; }

    public bool IsAdmin { get; set; }
    public bool IsModerator { get; set; }
    public bool IsActiveCreator { get; set; }
    public bool IsEmailConfirmed { get; set; }

    public int Platform { get; set; }
    public string PlatformId { get; set; } = string.Empty;

    public int StatusVisibility { get; set; }
    public int VrMovementMode { get; set; }
    public bool AvoidJuniors { get; set; }
    public bool IsRecentHistoryVisible { get; set; } = true;
    public bool DisallowInAppPurchases { get; set; }

    public int RemainingUsernameChanges { get; set; } = 10;

    public int RecCurrency { get; set; }
    public int RRecCurrency { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginTime { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenTime { get; set; }

    public bool IsBanned { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Session GUID the client sends as LoginLock; a second holder gets 409.</summary>
    public string? LoginLock { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}

/// <summary>A refresh token, hashed. Rotated on every use so a stolen copy is single-use.</summary>
public sealed class RefreshToken
{
    public long Id { get; set; }
    public int PlayerId { get; set; }
    public Player Player { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public string? LoginToken { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public int Platform { get; set; }
    public string PlatformId { get; set; } = string.Empty;
    public bool RequirePassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}

/// <summary>A single-use "open on the web" token, redeemed by grant_type=login_token.</summary>
public sealed class LoginToken
{
    public long Id { get; set; }
    public int PlayerId { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

/// <summary>A pending device-authorization request, polled by grant_type=device_code.</summary>
public sealed class DeviceAuthorization
{
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public int? ApprovedPlayerId { get; set; }
    public bool Denied { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A game room (Rec Room's "experience"), owned by a creator.</summary>
public sealed class Room
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ImageName { get; set; } = "none";
    public int CreatorAccountId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int State { get; set; }

    public int Accessibility { get; set; }
    public int MaxPlayers { get; set; }
    public int MinLevel { get; set; }
    public int MaxPlayerCalculationMode { get; set; }
    public int PersistenceVersion { get; set; }

    public bool SupportsJuniors { get; set; } = true;
    public bool SupportsScreens { get; set; }
    public bool SupportsTeleportVR { get; set; } = true;
    public bool SupportsWalkVR { get; set; } = true;
    public bool CloningAllowed { get; set; } = true;
    public bool DisableMicAutoMute { get; set; }
    public bool DisableRoomComments { get; set; }
    public bool EncryptVoiceChat { get; set; }
    public bool AllowNewUsers { get; set; } = true;
    public bool ToxmodEnabled { get; set; }

    public string TagsCsv { get; set; } = string.Empty;
    public string AutoTagsCsv { get; set; } = string.Empty;

    public int WarningMask { get; set; }
    public string CustomWarning { get; set; } = string.Empty;

    public string DataBlob { get; set; } = string.Empty;
    public string DataBlobHash { get; set; } = string.Empty;
    public string UnityAssetId { get; set; } = string.Empty;

    /// <summary>JSON array of {ImageName,Title,Subtitle}; serialized by hand, no separate table.</summary>
    public string LoadScreensJson { get; set; } = "[]";
    public string PromoImagesCsv { get; set; } = string.Empty;
    public string PromoExternalCsv { get; set; } = string.Empty;

    public bool IsRro { get; set; }

    public ICollection<SubRoom> SubRooms { get; set; } = new List<SubRoom>();
    public ICollection<RoomRoleAssignment> Roles { get; set; } = new List<RoomRoleAssignment>();
    public ICollection<RoomBan> Bans { get; set; } = new List<RoomBan>();
}

/// <summary>A scene inside a room. Rec Room calls these subrooms.</summary>
public sealed class SubRoom
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ImageName { get; set; } = string.Empty;
    public int MaxPlayers { get; set; }
    public int Accessibility { get; set; }
    public bool IsSpawnPoint { get; set; }
    public string DataBlob { get; set; } = string.Empty;
    public string DataBlobHash { get; set; } = string.Empty;
    public string PermissionsJson { get; set; } = "[]";
}

/// <summary>A committed subroom save, versioned and paginated through rooms/{id}/subrooms/{id}/saves.</summary>
public sealed class SubRoomDataSave
{
    public long Id { get; set; }
    public long SubRoomId { get; set; }
    public long RoomId { get; set; }
    public string UnityAssetId { get; set; } = string.Empty;
    public string DataBlob { get; set; } = string.Empty;
    public string DataBlobHash { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long SavedByAccountId { get; set; }
    public DateTime SavedOnPlatformUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A per-account role on a single room.</summary>
public sealed class RoomRoleAssignment
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public int AccountId { get; set; }
    public int Role { get; set; }
    public int InvitedRole { get; set; }
    public long LastChangedByAccountId { get; set; }
}

/// <summary>A room-scoped ban. Kicked players are told via a ModerationKick push, not a ban push.</summary>
public sealed class RoomBan
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public int BannedPlayerId { get; set; }
    public int BannedByPlayerId { get; set; }
    public int BanMask { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-room, per-player opaque state the room itself owns (CV2 style persistence).</summary>
public sealed class PlayerRoomData
{
    public long Id { get; set; }
    public int PlayerId { get; set; }
    public long RoomId { get; set; }
    public string Data { get; set; } = string.Empty;
}

/// <summary>Cheer / favorite / last-visit markers.</summary>
public sealed class RoomInteraction
{
    public long Id { get; set; }
    public int PlayerId { get; set; }
    public long RoomId { get; set; }
    public bool Cheered { get; set; }
    public bool Favorited { get; set; }
    public DateTime? LastVisitedAt { get; set; }
}

/// <summary>A live (or recently dead) instance of a room. Created by matchmaking.</summary>
public sealed class RoomInstance
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public long SubRoomId { get; set; }
    public string Location { get; set; } = string.Empty;
    public string PhotonRoomId { get; set; } = string.Empty;
    public string PhotonRegionId { get; set; } = "localhost";
    public string RoomCode { get; set; } = string.Empty;
    public string? CustomRoomCode { get; set; }
    public int MaxCapacity { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsFull { get; set; }
    public bool IsInProgress { get; set; }
    public bool EncryptVoiceChat { get; set; }
    public string? ClubId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A directed social edge. Blocked blocks both directions and beats friendship.</summary>
public sealed class Relationship
{
    public long Id { get; set; }
    public int AccountId { get; set; }
    public int TargetAccountId { get; set; }
    public bool IsFriend { get; set; }
    public bool IsBlocked { get; set; }
    public bool IsFavorite { get; set; }
    public bool Pending { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A friend request awaiting the target's answer.</summary>
public sealed class FriendRequest
{
    public long Id { get; set; }
    public int SenderAccountId { get; set; }
    public int ReceiverAccountId { get; set; }
    public bool Accepted { get; set; }
    public bool Declined { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A club. Membership and roles are per-account.</summary>
public sealed class Club
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string MainImageName { get; set; } = "none";
    public int CreatorAccountId { get; set; }
    public string Category { get; set; } = "recroomofficial";
    public int Visibility { get; set; }
    public int Joinability { get; set; }
    public bool AllowJuniors { get; set; } = true;
    public int MemberCount { get; set; }
    public int State { get; set; }
    public int ClubType { get; set; }
    public int MinLevel { get; set; }
    public bool ClubChatEnabled { get; set; } = true;
    public long? ClubhouseRoomId { get; set; }
    public bool IsRro { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ClubMember
{
    public long Id { get; set; }
    public long ClubId { get; set; }
    public int AccountId { get; set; }
    public int Role { get; set; }
    public bool ChatDisabled { get; set; }
    public bool NotificationsMuted { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A chat thread: a room's chat, a group's DM, or a club thread.</summary>
public sealed class Thread
{
    public long Id { get; set; }
    public string Type { get; set; } = "dm";
    public long? RoomId { get; set; }
    public long? ClubId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int CreatorAccountId { get; set; }
    public long? ParentThreadId { get; set; }
    public bool Announcement { get; set; }
    public bool Archived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ThreadMember
{
    public long Id { get; set; }
    public long ThreadId { get; set; }
    public int AccountId { get; set; }
    public bool Muted { get; set; }
    public DateTime? LastReadAt { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public long ThreadId { get; set; }
    public int SenderAccountId { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime SentTime { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}

/// <summary>A pinned note in a room's scene.</summary>
public sealed class RoomComment
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public long? SubRoomId { get; set; }
    public string Message { get; set; } = string.Empty;
    public int CreatorAccountId { get; set; }
    public int Color { get; set; }
    public string ImageName { get; set; } = "none";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One leaderboard stat channel for a player (or for a room).</summary>
public sealed class StatRecord
{
    public long Id { get; set; }
    public string StatName { get; set; } = string.Empty;
    public int PlayerId { get; set; }
    public long? RoomId { get; set; }
    public long Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A store product. Everything is free by default so nobody is stuck at a paywall.</summary>
public sealed class StoreItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ImageName { get; set; } = "none";
    public int ItemType { get; set; }
    public int Price { get; set; }
    public int CurrencyType { get; set; }
    public DateTime? SaleStart { get; set; }
    public DateTime? SaleEnd { get; set; }
    public int ConsumableType { get; set; }
    public bool Published { get; set; } = true;
}

/// <summary>Something an account owns: an avatar item, a consumable, a room key.</summary>
public sealed class InventoryEntry
{
    public long Id { get; set; }
    public int PlayerId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A per-player settings row (kept generic because the client's key set is not knowable).</summary>
public sealed class PlayerSetting
{
    public long Id { get; set; }
    public int PlayerId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>A moderation report against a player or a room.</summary>
public sealed class Report
{
    public long Id { get; set; }
    public int ReporterAccountId { get; set; }
    public int Category { get; set; }
    public string Details { get; set; } = string.Empty;
    public int? ReportedPlayerId { get; set; }
    public long? ReportedRoomId { get; set; }
    public string? ReportedImageName { get; set; }
    public bool Resolved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An issued content warning shown before a player enters moderation flows.</summary>
public sealed class PlayerWarning
{
    public long Id { get; set; }
    public int PlayerId { get; set; }
    public int WarningMask { get; set; }
    public string CustomWarning { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAt { get; set; }
}

/// <summary>An uploaded blob: profile pictures, banners, room images, invention assets.</summary>
public sealed class Blob
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public int OwnerAccountId { get; set; }
    /// <summary>Opaque access level used by api/images/v1/modifyaccessibility.</summary>
    public int Accessibility { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An announcement post in a club.</summary>
public sealed class Announcement
{
    public long Id { get; set; }
    public long ClubId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ImageName { get; set; } = "none";
    public int CreatorAccountId { get; set; }
    public bool Pinned { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A player's quest/progression counters, surfaced by the progression routes.</summary>
public sealed class Progression
{
    public long Id { get; set; }
    public int PlayerId { get; set; }
    public long Level { get; set; } = 1;
    public long Experience { get; set; }
    public string QuestProgressJson { get; set; } = "{}";
    public int WeeklyChallengePoints { get; set; }
}

/// <summary>A created object: room, invention, playlist. Kept for the invention and playlist routes.</summary>
public sealed class Creation
{
    public long Id { get; set; }
    public string Type { get; set; } = "room";
    public int CreatorAccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ImageName { get; set; } = "none";
    public string DataBlob { get; set; } = string.Empty;
    public string TagsCsv { get; set; } = string.Empty;
    public bool Published { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An uploaded video record (metadata only; bytes live in the blob store).</summary>
public sealed class VideoRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CreatorAccountId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ImageName { get; set; } = "none";
    public int LengthSeconds { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Server-wide operator settings, editable from the admin surface.</summary>
public sealed class ServerSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
