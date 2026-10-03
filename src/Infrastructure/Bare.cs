using System.Text.Json.Nodes;

namespace RecEmu.Server.Infrastructure;

/// <summary>
/// Responses for the route family that silently breaks everything else: the ones whose client
/// method returns a scalar.
///
/// These read a bare <c>123</c> / <c>true</c> / <c>"text"</c> straight off the stream with a
/// strict reader. Wrapping the same value in an object — returning <c>{"Limit":1000}</c> where
/// the client reads <c>1000</c> — makes deserialization throw inside the continuation, and
/// because the client swallows that, the feature just never works. Nothing logs an error, so
/// these are the easiest routes in the contract to get wrong and the hardest to notice.
/// </summary>
public static class Bare
{
    public static IResult Int(int value) => Raw(value.ToString());

    public static IResult Long(long value) => Raw(value.ToString());

    public static IResult Bool(bool value) => Raw(value ? "true" : "false");

    /// <summary>A bare JSON string, quoted and escaped as JSON (not a bare token).</summary>
    public static IResult Text(string value) => Raw(JsonSerializerShim.Quote(value));

    /// <summary>An object whose single property the client projects down to a scalar.</summary>
    public static IResult Wrapped(string key, object? value)
        => Raw(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { [key] = value }, Json.Options));

    public static IResult Raw(string json) => Results.Text(json, "application/json; charset=utf-8");

    public static IResult Ok() => Results.StatusCode(StatusCodes.Status200OK);
}

/// <summary>A single-key object, for the many readers that deserialize into a small DTO.</summary>
public static class Obj
{
    public static Dictionary<string, object?> Of(string key, object? value)
        => new(StringComparer.Ordinal) { [key] = value };

    /// <summary>
    /// Builds an object from an ordered key/value list. Used where a DTO would carry twenty
    /// optional fields and the client defaults them anyway.
    /// </summary>
    public static Dictionary<string, object?> Create(params (string Key, object? Value)[] pairs)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs) result[key] = value;
        return result;
    }

    public static List<T> List<T>(params T[] items) => items.ToList();

    public static Dictionary<string, object?> Paged<T>(IEnumerable<T> results, int total)
        => Create(("Results", results.ToList()), ("TotalResults", total));
}

internal static class JsonSerializerShim
{
    public static string Quote(string value)
        => System.Text.Json.JsonSerializer.Serialize(value, Json.Options);
}

/// <summary>
/// Well-known enum values, kept as named constants rather than magic numbers at the call site.
/// The client sends these as integers; the numeric identity is part of the contract, so renaming
/// a member is safe and changing its value is not.
/// </summary>
public static class Enums
{
    /// <summary>PHOGEIPMHOK — api/versioncheck response.</summary>
    public static class VersionStatus
    {
        public const int Ok = 0;
        public const int UpdateRequired = 1;
        public const int Unsupported = 2;
    }

    /// <summary>HMIBNIEBEKK — matchmake/* errorCode.</summary>
    public static class MatchError
    {
        public const int Ok = 0;
        public const int RoomDoesNotExist = 4;
        public const int RoomInstanceDoesNotExist = 5;
        public const int RoomIsFull = 6;
        public const int RoomInstanceIsPrivate = 26;
        public const int RoomCodeIsInvalid = 40;
        public const int NotEnoughPermissions = 41;
        public const int Failed = 50;
    }

    /// <summary>JoinMode on matchmake/room — 0 joins a shared instance, 1/2 create new ones.</summary>
    public static class JoinMode
    {
        public const int JoinShared = 0;
        public const int NewPublic = 1;
        public const int NewPrivate = 2;
    }

    /// <summary>OMMBGJMJJPN — per-room role assignment.</summary>
    public static class RoomRole
    {
        public const int None = 0;
        public const int Host = 1;
        public const int Moderator = 2;
        public const int CoOwner = 3;
    }

    /// <summary>Accessibility of a room or subroom.</summary>
    public static class Accessibility
    {
        public const int Public = 0;
        public const int FriendsOfFriends = 1;
        public const int InviteOnly = 2;
        public const int Hidden = 3;
    }

    /// <summary>DDIFIKACNBN — how MaxPlayers is derived.</summary>
    public static class MaxPlayerCalculationMode
    {
        public const int Constant = 0;
        public const int RoomCapacity = 1;
    }

    /// <summary>DCDPJPHBHOA — room ban mask.</summary>
    public static class BanMask
    {
        public const int None = 0;
        public const int Join = 1;
        public const int VoiceChat = 2;
        public const int TextChat = 4;
    }

    /// <summary>NGPHEBMBPIC — room content-warning mask.</summary>
    public static class WarningMask
    {
        public const int None = 0;
        public const int Profanity = 1;
        public const int SexualContent = 2;
        public const int Gore = 4;
        public const int Loud = 8;
        public const int Distress = 16;
        public const int Discrimination = 32;
    }

    /// <summary>StatusVisibility — who may see the player online.</summary>
    public static class StatusVisibility
    {
        public const int Everyone = 0;
        public const int FriendsOfFriends = 1;
        public const int Friends = 2;
        public const int Nobody = 3;
    }

    /// <summary>VRMovementMode.</summary>
    public static class VrMovementMode
    {
        public const int Default = 0;
        public const int SmoothLocomotion = 1;
        public const int Teleport = 2;
    }

    /// <summary>Club joinability, for clubs/{id}/members/requesttojoin.</summary>
    public static class ClubJoinability
    {
        public const int Open = 0;
        public const int InviteOnly = 1;
        public const int AskToJoin = 2;
    }
}
