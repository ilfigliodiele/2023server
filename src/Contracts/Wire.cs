using System.Globalization;
using System.Text.Json.Nodes;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

// The chat thread entity collides with System.Threading.Thread under implicit usings; the entity is
// what every reference in here means.
using Thread = RecEmu.Server.Data.Thread;

namespace RecEmu.Server.Contracts;

/// <summary>
/// Maps entities onto the client's DTO shapes.
///
/// Keys are written out by hand in PascalCase rather than produced by a naming policy. That is
/// deliberate: the client registers each property under exactly three spellings — PascalCase,
/// camelCase and all-lowercase — and matches case-sensitively against only those. A serializer
/// configured with the wrong policy would emit a fourth spelling that the client drops silently,
/// leaving the CLR default and no error anywhere. Spelling the keys out makes that class of bug
/// visible in review.
///
/// Two response-shape traps are also encoded here. Several rooms mutations return a *slim* room
/// object and the client re-parses full details after every mutation, NRE-ing on the missing
/// nested Stats/SubRooms/Roles/Tags — so every mutation returns <see cref="RoomDetails"/>. And
/// RankingContext is read as an opaque string by the client; a JSON object there breaks every
/// room-details parse at once.
/// </summary>
public static class Wire
{
    public static string Iso(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string? Iso(DateTime? value) => value is null ? null : Iso(value.Value);

    /// <summary>
    /// MIMFEOHELNC — the public account projection. UserName's Pascal spelling is what the client
    /// registers; "Username" is not among its three accepted spellings and would deserialize to null.
    /// </summary>
    public static Dictionary<string, object?> Account(Player player) => Obj.Create(
        ("AccountId", player.Id),
        ("UserName", player.Username),
        ("DisplayName", player.DisplayName),
        ("DisplayEmoji", player.DisplayEmoji),
        ("ProfileImage", player.ProfileImage),
        ("BannerImage", player.BannerImage),
        ("TreatAsJunior", player.IsJunior),
        ("HasBirthday", player.Birthday is not null),
        ("PersonalPronouns", player.PersonalPronouns),
        ("IdentityFlags", player.IdentityFlags));

    /// <summary>
    /// CAALNOGGDLG — account/me. Emits every key the reader registers: the ones a client that
    /// never sets a field leaves at its default are still emitted, because the settings screen
    /// otherwise shows empty rows for values the player did save.
    /// </summary>
    public static Dictionary<string, object?> SelfAccount(Player player)
    {
        var account = Obj.Create(
            ("AccountId", player.Id),
            ("UserName", player.Username),
            ("RawUserName", player.Username),
            ("DisplayName", player.DisplayName),
            ("DisplayEmoji", player.DisplayEmoji),
            ("ProfileImage", player.ProfileImage),
            ("BannerImage", player.BannerImage),
            ("TreatAsJunior", player.IsJunior),
            ("HasBirthday", player.Birthday is not null),
            ("PersonalPronouns", player.PersonalPronouns),
            ("IdentityFlags", player.IdentityFlags),
            ("AvailableUsernameChanges", player.RemainingUsernameChanges),
            ("Email", player.Email),
            ("Phone", player.Phone),
            ("JuniorState", player.JuniorState),
            ("ParentAccountId", player.ParentAccountId),
            ("IsJunior", player.IsJunior),
            ("CreatedAt", Iso(player.CreatedAt)),
            ("IsRecentHistoryVisible", player.IsRecentHistoryVisible),
            ("Platforms", Array.Empty<object>()));

        // Omitting a non-nullable DateTime is safe; emitting null for it throws in the reader, so
        // a player who never set a birthday gets no key at all rather than an explicit null.
        if (player.Birthday is not null) account["Birthday"] = Iso(player.Birthday.Value);

        return account;
    }

    /// <summary>EBPCGLAICIH — accountprivacysettings.</summary>
    public static Dictionary<string, object?> PrivacySettings(Player player) => Obj.Create(
        ("AccountId", player.Id),
        ("IsRecentHistoryVisible", player.IsRecentHistoryVisible));

    /// <summary>MFBCJHHEMKF — parentalcontrol/me.</summary>
    public static Dictionary<string, object?> ParentalControl(Player player) => Obj.Create(
        ("AccountId", player.Id),
        ("DisallowInAppPurchases", player.DisallowInAppPurchases));

    /// <summary>CCNKBIGMLGP — account/{id}/bio.</summary>
    public static Dictionary<string, object?> Bio(int accountId, string bio)
        => Obj.Create(("AccountId", accountId), ("Bio", bio));

    /// <summary>
    /// NEMINAEBALC — the room summary used by every list route. Emits the optional summary keys
    /// too, so rows are not missing MaxPlayers/MinLevel where the reader expects them.
    /// </summary>
    public static Dictionary<string, object?> RoomSummary(Room room) => Obj.Create(
        ("RoomId", room.Id),
        ("Name", room.Name),
        ("Description", room.Description),
        ("ImageName", room.ImageName),
        ("CreatorAccountId", room.CreatorAccountId),
        ("CreatedAt", Iso(room.CreatedAt)),
        ("State", room.State),
        ("Accessibility", room.Accessibility),
        ("MaxPlayers", room.MaxPlayers),
        ("MinLevel", room.MinLevel),
        ("MaxPlayerCalculationMode", room.MaxPlayerCalculationMode),
        ("PersistenceVersion", room.PersistenceVersion),
        ("ToxmodEnabled", room.ToxmodEnabled),
        ("SupportsJuniors", room.SupportsJuniors),
        ("SupportsScreens", room.SupportsScreens),
        ("SupportsTeleportVR", room.SupportsTeleportVR),
        ("SupportsWalkVR", room.SupportsWalkVR),
        ("CloningAllowed", room.CloningAllowed),
        ("DisableMicAutoMute", room.DisableMicAutoMute),
        ("DisableRoomComments", room.DisableRoomComments),
        ("EncryptVoiceChat", room.EncryptVoiceChat),
        ("AllowNewUsers", room.AllowNewUsers),
        ("IsRro", room.IsRro),
        ("UnityAssetId", room.UnityAssetId));

    /// <summary>
    /// FGCPNAACHIK — full room details, and the response body for every room mutation.
    ///
    /// SubRooms, Roles, Tags, Stats and Warnings are always present, even when empty: the client
    /// walks them after each mutation and a null there throws inside a dispose path, which surfaces
    /// as the room appearing to have failed to save despite having saved.
    /// </summary>
    public static Dictionary<string, object?> RoomDetails(
        Room room,
        IEnumerable<SubRoom>? subRooms = null,
        IEnumerable<RoomRoleAssignment>? roles = null)
    {
        var details = RoomSummary(room);

        details["DataBlob"] = room.DataBlob;
        details["DataBlobHash"] = room.DataBlobHash;
        details["LoadScreens"] = ParseArray(room.LoadScreensJson).Select(node => Obj.Create(
            ("ImageName", node?["ImageName"]?.ToString() ?? "none"),
            ("Title", node?["Title"]?.ToString() ?? string.Empty),
            ("Subtitle", node?["Subtitle"]?.ToString() ?? string.Empty))).ToList();
        details["PromoImages"] = SplitCsv(room.PromoImagesCsv);
        details["PromoExternal"] = ParseArray(room.PromoExternalCsv).Select(node => Obj.Create(
            ("Type", node?["Type"]?.GetValue<int>() ?? 0),
            ("Reference", node?["Reference"]?.ToString() ?? string.Empty))).ToList();

        // Read as an opaque string by the client. A JSON object here breaks every room-details
        // parse at once, so it is emitted as text and never as a nested object.
        details["RankingContext"] = string.Empty;

        details["SubRooms"] = (subRooms ?? room.SubRooms).Select(SubRoomSummary).ToList();
        details["Roles"] = (roles ?? room.Roles).Select(RoleRow).ToList();
        details["Tags"] = SplitCsv(room.TagsCsv);
        details["AutoTags"] = SplitCsv(room.AutoTagsCsv);
        details["Warnings"] = new List<object>();
        details["Stats"] = Obj.Create(
            ("RoomId", room.Id),
            ("Upvotes", 0),
            ("Downvotes", 0),
            ("VisitCount", 0),
            ("CheerCount", 0),
            ("FavoriteCount", 0),
            ("Occupancy", 0),
            ("Rating", 0.0));
        details["Warning"] = Obj.Create(
            ("WarningMask", room.WarningMask),
            ("CustomWarning", room.CustomWarning));

        return details;
    }

    public static Dictionary<string, object?> SubRoomSummary(SubRoom subRoom) => Obj.Create(
        ("SubRoomId", subRoom.Id),
        ("Name", subRoom.Name),
        ("Description", subRoom.Description),
        ("ImageName", subRoom.ImageName),
        ("MaxPlayers", subRoom.MaxPlayers),
        ("Accessibility", subRoom.Accessibility),
        ("CanMatchmakeInto", subRoom.Accessibility == Enums.Accessibility.Public),
        ("IsSpawnPoint", subRoom.IsSpawnPoint),
        ("DataBlob", subRoom.DataBlob),
        ("DataBlobHash", subRoom.DataBlobHash),
        ("Permissions", ParseArray(subRoom.PermissionsJson)));

    /// <summary>EFHPLDPNGIM — one row of rooms/{id}/roles.</summary>
    public static Dictionary<string, object?> RoleRow(RoomRoleAssignment role) => Obj.Create(
        ("AccountId", role.AccountId),
        ("Role", role.Role),
        ("InvitedRole", role.InvitedRole),
        ("LastChangedByAccountId", role.LastChangedByAccountId));

    /// <summary>IBHAKOOKEEE — one row of rooms/{id}/bans.</summary>
    public static Dictionary<string, object?> BanRow(RoomBan ban) => Obj.Create(
        ("AccountId", ban.BannedPlayerId),
        ("BannedByAccountId", ban.BannedByPlayerId),
        ("BanStartTime", Iso(ban.CreatedAt)));

    /// <summary>CNINIABILDI — rooms/{id}/interactionby/me.</summary>
    public static Dictionary<string, object?> Interaction(bool cheered, bool favorited, DateTime? lastVisited)
        => Obj.Create(("Cheered", cheered), ("Favorited", favorited), ("LastVisitedAt", Iso(lastVisited)));

    /// <summary>ILKMFMCOPPO — rooms/{id}/playerdata/me.</summary>
    public static Dictionary<string, object?> PlayerRoomData(string data)
        => Obj.Create(("Data", data));

    /// <summary>AKCLLEJNFFD — api/rooms/v1/filters.</summary>
    public static Dictionary<string, object?> RoomFilters(IReadOnlyList<string> tags) => Obj.Create(
        ("PinnedFilters", tags.Take(6).ToList()),
        ("PopularFilters", tags.Take(12).ToList()),
        ("TrendingFilters", tags.Take(12).ToList()));

    /// <summary>PNDCMIMEJLD — room/{id}/instances.</summary>
    public static Dictionary<string, object?> InstanceSummary(RoomInstance instance, IEnumerable<int> playerIds)
        => Obj.Create(
            ("RoomInstanceId", instance.Id),
            ("RoomId", instance.RoomId),
            ("SubRoomId", instance.SubRoomId),
            ("PlayerIds", playerIds.ToList()),
            ("IsFull", instance.IsFull),
            ("CreatedAt", Iso(instance.CreatedAt)));

    /// <summary>
    /// HCHDEHIGEBE — the roomInstance inside every matchmake response. The client deserializes it
    /// here wherever matchmaking data appears, so the key set is fixed by that reader.
    /// </summary>
    public static Dictionary<string, object?> RoomInstance(
        RoomInstance instance,
        PhotonOptions photon,
        string roomName,
        bool isPrivate,
        int playerCount)
        => Obj.Create(
            ("ClubId", instance.ClubId ?? "0"),
            ("EncryptVoiceChat", instance.EncryptVoiceChat),
            ("EventId", "0"),
            ("IsFull", instance.IsFull),
            ("IsInProgress", instance.IsInProgress),
            ("IsPrivate", isPrivate),
            ("Location", instance.Location),
            ("MaxCapacity", instance.MaxCapacity),
            ("Name", roomName),
            ("PhotonRegionId", string.IsNullOrEmpty(instance.PhotonRegionId) ? photon.Region : instance.PhotonRegionId),
            ("PhotonRoomId", instance.PhotonRoomId),
            ("RoomCode", instance.CustomRoomCode ?? instance.RoomCode),
            ("RoomId", instance.RoomId),
            ("RoomInstanceId", instance.Id),
            ("RoomInstanceType", 0),
            ("SubRoomId", instance.SubRoomId),
            ("PlayerCount", playerCount));

    /// <summary>PCFNLCMMGKB — the matchmake response envelope. errorCode 0 means success.</summary>
    public static Dictionary<string, object?> MatchmakingResponse(int errorCode, Dictionary<string, object?>? roomInstance)
        => Obj.Create(("errorCode", errorCode), ("roomInstance", roomInstance ?? new Dictionary<string, object?>()));

    /// <summary>FOIJDINBPFG — a club as seen from account/{id}/clubs and club listings.</summary>
    public static Dictionary<string, object?> Club(Club club) => Obj.Create(
        ("ClubId", club.Id),
        ("Name", club.Name),
        ("Description", club.Description),
        ("MainImageName", club.MainImageName),
        ("State", club.State),
        ("CreatorAccountId", club.CreatorAccountId),
        ("Category", club.Category),
        ("Visibility", club.Visibility),
        ("Joinability", club.Joinability),
        ("AllowJuniors", club.AllowJuniors),
        ("MemberCount", club.MemberCount),
        ("IsRro", club.IsRro),
        ("ClubhouseRoomId", club.ClubhouseRoomId ?? 0),
        ("ClubType", club.ClubType),
        // Both of these default to 0/false in the client reader, which renders club chat as
        // disabled on every club in the profile's Clubs tab if they are omitted.
        ("MinLevel", club.MinLevel),
        ("ClubChatEnabled", club.ClubChatEnabled));

    /// <summary>JFHFGHFGDGF style club membership row.</summary>
    public static Dictionary<string, object?> ClubMemberRow(ClubMember member, Club club) => Obj.Create(
        ("AccountId", member.AccountId),
        ("ClubId", club.Id),
        ("Role", member.Role),
        ("ChatDisabled", member.ChatDisabled),
        ("NotificationsMuted", member.NotificationsMuted),
        ("JoinedAt", Iso(member.JoinedAt)));

    public static Dictionary<string, object?> StoreItemRow(StoreItem item) => Obj.Create(
        ("StoreItemId", item.Id),
        ("Name", item.Name),
        ("Description", item.Description),
        ("ImageName", item.ImageName),
        ("ItemType", item.ItemType),
        ("Price", item.Price),
        ("CurrencyType", item.CurrencyType),
        ("SaleStart", Iso(item.SaleStart)),
        ("SaleEnd", Iso(item.SaleEnd)),
        ("ConsumableType", item.ConsumableType));

    public static Dictionary<string, object?> InventoryRow(InventoryEntry entry) => Obj.Create(
        ("ItemId", entry.ItemId),
        ("Category", entry.Category),
        ("Quantity", entry.Quantity),
        ("AcquiredAt", Iso(entry.AcquiredAt)));

    public static Dictionary<string, object?> ChatMessageRow(ChatMessage message, Player? sender) => Obj.Create(
        ("MessageId", message.Id),
        ("ThreadId", message.ThreadId),
        ("SenderAccountId", message.SenderAccountId),
        ("SenderUsername", sender?.Username ?? string.Empty),
        ("SenderDisplayName", sender?.DisplayName ?? string.Empty),
        ("SenderProfileImage", sender?.ProfileImage ?? "none"),
        ("Message", message.Message),
        ("SentTime", Iso(message.SentTime)),
        ("Deleted", message.DeletedAt is not null));

    public static Dictionary<string, object?> CommentRow(RoomComment comment, Player? author) => Obj.Create(
        ("RoomCommentId", comment.Id),
        ("RoomId", comment.RoomId),
        ("SubRoomId", comment.SubRoomId ?? 0),
        ("Message", comment.Message),
        ("CreatorAccountId", comment.CreatorAccountId),
        ("CreatorUsername", author?.Username ?? string.Empty),
        ("Color", comment.Color),
        ("ImageName", comment.ImageName),
        ("CreatedAt", Iso(comment.CreatedAt)));

    public static Dictionary<string, object?> AnnouncementRow(Announcement announcement) => Obj.Create(
        ("AnnouncementId", announcement.Id),
        ("ClubId", announcement.ClubId),
        ("Title", announcement.Title),
        ("Message", announcement.Message),
        ("ImageName", announcement.ImageName),
        ("CreatorAccountId", announcement.CreatorAccountId),
        ("Pinned", announcement.Pinned),
        ("CreatedAt", Iso(announcement.CreatedAt)));

    /// <summary>COGJIOGPNGD — one featured-room tile. RoomName is a distinct key from Name.</summary>
    public static Dictionary<string, object?> FeaturedRoomTile(Room room) => Obj.Create(
        ("ImageName", room.ImageName),
        ("RoomId", room.Id),
        ("RoomName", room.Name));

    public static Dictionary<string, object?> ThreadRow(Thread thread) => Obj.Create(
        ("ThreadId", thread.Id),
        ("Type", thread.Type),
        ("RoomId", thread.RoomId ?? 0),
        ("ClubId", thread.ClubId ?? 0),
        ("Name", thread.Name),
        ("Description", thread.Description),
        ("CreatorAccountId", thread.CreatorAccountId),
        ("ParentThreadId", thread.ParentThreadId ?? 0),
        ("IsAnnouncement", thread.Announcement),
        ("CreatedAt", Iso(thread.CreatedAt)));

    /// <summary>BGIOGDKBIO — a subroom data save row.</summary>
    public static Dictionary<string, object?> SubRoomSaveRow(SubRoomDataSave save) => Obj.Create(
        ("SubRoomDataSaveId", save.Id),
        ("SubRoomId", save.SubRoomId),
        ("UnityAssetId", save.UnityAssetId),
        ("DataBlob", save.DataBlob),
        ("DataBlobHash", save.DataBlobHash),
        ("Description", save.Description),
        ("SavedByAccountId", save.SavedByAccountId),
        ("SavedOnPlatform", Iso(save.SavedOnPlatformUtc)),
        ("DescriptionSet", !string.IsNullOrEmpty(save.Description)));

    public static Dictionary<string, object?> ReportRow(Report report) => Obj.Create(
        ("ReportId", report.Id),
        ("ReporterAccountId", report.ReporterAccountId),
        ("Category", report.Category),
        ("Details", report.Details),
        ("ReportedPlayerId", report.ReportedPlayerId ?? 0),
        ("ReportedRoomId", report.ReportedRoomId ?? 0),
        ("Resolved", report.Resolved),
        ("CreatedAt", Iso(report.CreatedAt)));

    /// <summary>Friend / relationship row as the social graph routes return it.</summary>
    public static Dictionary<string, object?> RelationshipRow(Player other, bool isFriend, bool isFavorite, bool pending)
        => Obj.Create(
            ("AccountId", other.Id),
            ("UserName", other.Username),
            ("DisplayName", other.DisplayName),
            ("ProfileImage", other.ProfileImage),
            ("IsFriend", isFriend),
            ("IsFavorite", isFavorite),
            ("Pending", pending),
            ("StatusVisibility", other.StatusVisibility));

    /// <summary>GFMBGJEEFB — the season pass / membership record.</summary>
    public static Dictionary<string, object?> SeasonPass(Player player, RecEmuOptionsAccessor options) => Obj.Create(
        ("IsActive", false),
        ("Tier", 0),
        ("RecRoomPremium", false),
        ("AppVersion", options.Value.AppVersion));

    private static IEnumerable<string> SplitCsv(string csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<JsonNode?> ParseArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<JsonNode?>();
        return Json.Parse(json) is JsonArray array ? array : Array.Empty<JsonNode?>();
    }

    public static string WriteArray<T>(IEnumerable<T> items)
        => System.Text.Json.JsonSerializer.Serialize(items, Infrastructure.Json.Options);

    public static string Csv(IEnumerable<string> values)
        => string.Join(',', values.Select(v => v?.Trim() ?? string.Empty).Where(v => v.Length > 0));
}
