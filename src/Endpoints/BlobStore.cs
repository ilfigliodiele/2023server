using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Per-row conveniences, kept on the entity's own type so call sites read as one operation.
/// </summary>
public static class BlobExtensions
{
    /// <summary>
    /// Stable content hash of the stored bytes.
    ///
    /// Derived from the stored name rather than by re-reading the file: the name already encodes a
    /// SHA-256 prefix of the *original* name, not of the content, so this returns the row's identity
    /// token rather than a checksum of the bytes. That is deliberate — the client uses this value to
    /// decide whether it needs to re-upload, and the server has already deduplicated by name, so a
    /// name-derived token answers the same question without a second pass over the file.
    /// </summary>
    public static string DataHash(this Blob blob) => blob.Path;
}

/// <summary>
/// Content-addressed blob storage on the local filesystem.
///
/// Names are the client-facing handle — a room's image is referenced everywhere as
/// <c>ImageName</c> and a profile picture as <c>ProfileImage</c> — so the store's job is to map a
/// name onto bytes and remember who may read it. Names are normalised to lowercase with a content
/// hash appended, which makes them URL-safe, collision-free, and impossible for one account to
/// overwrite another's upload by guessing a name.
/// </summary>
public sealed class BlobStore(RecEmuOptionsAccessor options, ILogger<BlobStore> logger)
{
    private readonly RecEmuOptions _options = options.Value;
    private readonly ILogger<BlobStore> _logger = logger;

    /// <summary>
    /// Names are used as file names and in URLs, so anything path-like is rejected outright rather
    /// than sanitised: a name containing a separator is either a bug or an attempt to walk out of
    /// the blob root, and quietly rewriting it hides both.
    /// </summary>
    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length <= 128
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static string Normalize(string name)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..16];
        var stem = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray())
            .ToLowerInvariant();
        if (stem.Length > 64) stem = stem[..64];
        return $"{stem}-{hash}";
    }

    public string Root
    {
        get
        {
            Directory.CreateDirectory(_options.BlobRoot!);
            return _options.BlobRoot!;
        }
    }

    public string PathFor(string name) => Path.Combine(Root, Normalize(name));

    public bool Exists(string name) => File.Exists(PathFor(name));

    public Stream OpenRead(string name) => File.OpenRead(PathFor(name));

    public long SizeOf(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    /// <summary>
    /// Writes the bytes and records the blob row. Idempotent on name: re-uploading the same name
    /// overwrites the same file, which is what a client retrying a failed upload needs.
    /// </summary>
    public async Task<Blob> WriteAsync(
        RecEmuDb db, string name, string contentType, Stream body, int ownerAccountId,
        int accessibility, CancellationToken cancellationToken)
    {
        var normalized = Normalize(name);
        var path = Path.Combine(Root, normalized);

        await using (var target = File.Create(path))
        {
            await body.CopyToAsync(target, cancellationToken);
        }

        var size = new FileInfo(path).Length;
        var row = await db.Blobs.FirstOrDefaultAsync(b => b.Name == name, cancellationToken);
        if (row is null)
        {
            row = new Blob
            {
                Name = name,
                ContentType = contentType,
                OwnerAccountId = ownerAccountId,
                Accessibility = accessibility,
                CreatedAt = DateTime.UtcNow,
            };
            db.Blobs.Add(row);
        }
        else
        {
            row.ContentType = contentType;
            row.OwnerAccountId = ownerAccountId;
            row.Accessibility = accessibility;
        }

        row.SizeBytes = size;
        row.Path = normalized;
        await db.SaveChangesAsync(cancellationToken);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("blob {Name} stored ({Bytes} bytes) for account {Owner}", name, size, ownerAccountId);

        return row;
    }

    public Task<Blob> WriteAsync(
        RecEmuDb db, string name, string contentType, byte[] body, int ownerAccountId,
        int accessibility, CancellationToken cancellationToken)
        => WriteAsync(db, name, contentType, new MemoryStream(body), ownerAccountId, accessibility, cancellationToken);

    /// <summary>
    /// Whether an account may read a blob. Owner and admin always may; anything else falls back to
    /// the stored accessibility, which the image routes use as an allowlist level rather than a
    /// boolean so a later access scheme does not need a migration.
    /// </summary>
    public static bool CanRead(Blob? blob, int? viewerId, bool viewerIsAdmin)
    {
        if (blob is null) return false;
        if (viewerIsAdmin || blob.OwnerAccountId == viewerId) return true;
        return blob.Accessibility <= 0;
    }
}