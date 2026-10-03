using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Blob storage for things that are not images: room scene assets, invention bundles, videos.
///
/// The upload route answers with a name rather than a URL, and the client then refers to the asset by
/// that name from <c>UnityAssetId</c>. That indirection is deliberate in the original protocol and
/// cheap to keep here: it means a scene upload can be replaced without the room row changing, and it
/// means the CDN URL is derived at read time rather than stored where it can go stale.
/// </summary>
public static class StorageEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/storage/v1/upload", async (
            HttpContext http, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var name = (await http.ReadStringAsync("name", "filename", "assetName"))?.Trim() ?? string.Empty;
            if (!BlobStore.IsValidName(name)) return Results.BadRequest();

            var contentType = http.Request.ContentType ?? "application/octet-stream";
            var blob = await blobs.WriteAsync(db, name, contentType, http.Request.Body, caller.Id, 1, ct);

            // Hash of the bytes, so the client can skip re-uploading an asset it already sent. The
            // protocol expects this as "hash"; it is not the same value as DataBlobHash, which
            // refers to the client's own serialised scene rather than to the uploaded bytes.
            return Results.Json(Obj.Create(
                ("name", blob.Name),
                ("hash", blob.DataHash()),
                ("size", blob.SizeBytes)), Json.Options);
        });

        app.MapGet("/api/storage/v1/{name}", async (
            string name, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            if (!BlobStore.IsValidName(name)) return Results.NotFound();

            var blob = await db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.Name == name, ct);
            if (blob is null || !blobs.Exists(name)) return Results.NotFound();

            var viewer = await current.GetAsync(ct);
            if (!BlobStore.CanRead(blob, viewer?.Id, viewer?.IsAdmin == true)) return Results.Forbid();

            return Results.File(blobs.OpenRead(name), blob.ContentType, name);
        });

        app.MapGet("/api/storage/v1/name/{name}/hash", async (string name, RecEmuDb db, CancellationToken ct) =>
        {
            var blob = await db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.Name == name, ct);
            return blob is null ? Results.NotFound() : Bare.Text(blob.DataHash());
        });

        app.MapDelete("/api/storage/v1/{name}", async (
            string name, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Name == name, ct);
            if (blob is null) return Bare.Ok();
            if (blob.OwnerAccountId != caller.Id && !caller.IsAdmin) return Results.Forbid();

            db.Blobs.Remove(blob);
            await db.SaveChangesAsync(ct);

            var path = blobs.PathFor(name);
            if (File.Exists(path)) File.Delete(path);

            return Bare.Ok();
        });

        // The client's own inventory of what it may upload to. Returning the account's own blobs
        // rather than an empty list makes the storage browser show something before the first
        // upload, which is where a new player looks to confirm uploads worked at all.
        app.MapGet("/api/storage/v1/me", async (
            HttpContext http, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);
            var skip = await http.ReadIntAsync("skip") ?? 0;
            var take = Math.Clamp(await http.ReadIntAsync("take") ?? 50, 1, 200);

            var rows = await db.Blobs.AsNoTracking()
                .Where(b => b.OwnerAccountId == caller.Id)
                .OrderByDescending(b => b.Id)
                .ToListAsync(ct);

            var page = rows.Skip(skip).Take(take).Select(b => Obj.Create(
                ("Name", b.Name),
                ("SizeBytes", b.SizeBytes),
                ("ContentType", b.ContentType),
                ("CreatedAt", b.CreatedAt.ToString("o")))).ToList();

            return Results.Json(Obj.Paged(page, rows.Count), Json.Options);
        });
    }
}