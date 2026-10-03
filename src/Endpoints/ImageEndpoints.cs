using Microsoft.EntityFrameworkCore;
using RecEmu.Server.Auth;
using RecEmu.Server.Data;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Realtime;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Image upload, retrieval and accessibility.
///
/// Names are opaque client handles rather than URLs. The client asks for <c>/images/name/&lt;x&gt;</c>
/// and the server resolves the bytes; that indirection is what makes accessibility changeable — a
/// picture can be made private without rewriting the reference stored on a room row, which is why
/// the access level lives on the blob rather than being baked into the name.
/// </summary>
public static class ImageEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/images/name/{name}", async (
            string name, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
            await ServeAsync(name, blobs, db, current, ct));

        app.MapGet("/api/images/v1/name/{name}", async (
            string name, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
            await ServeAsync(name, blobs, db, current, ct));

        // The CDN host serves the same bytes under a different path. The client requests CDN URLs
        // for anything cached, so a server that answers the img host but 404s the CDN host produces
        // a game where profile pictures load in some screens and not others.
        app.MapGet("/cdn/v1/name/{name}", async (
            string name, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
            await ServeAsync(name, blobs, db, current, ct));

        app.MapPost("/api/images/v1/upload", async (
            HttpContext http, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var name = (await http.ReadStringAsync("name", "imageName"))?.Trim() ?? string.Empty;
            if (!BlobStore.IsValidName(name)) return Results.BadRequest();

            var accessibility = await http.ReadIntAsync("accessibility") ?? 1;
            var contentType = http.Request.ContentType ?? "application/octet-stream";

            var blob = await blobs.WriteAsync(db, name, contentType, http.Request.Body, caller.Id, accessibility, ct);

            return Results.Json(Obj.Create(
                ("Name", blob.Name),
                ("ImageName", blob.Name),
                ("SizeBytes", blob.SizeBytes),
                ("Accessibility", blob.Accessibility)), Json.Options);
        });

        app.MapMethods("/api/images/v1/{name}/modify", ["PUT", "POST"], async (
            HttpContext http, string name, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Name == name, ct);
            if (blob is null) return Results.NotFound();
            if (blob.OwnerAccountId != caller.Id && !caller.IsAdmin) return Results.Forbid();

            var replacement = (await http.ReadStringAsync("name", "imageName"))?.Trim();
            if (!string.IsNullOrWhiteSpace(replacement))
            {
                if (!BlobStore.IsValidName(replacement)) return Results.BadRequest();
                blob.Name = replacement!;
            }

            if (await http.HasKeyAsync("accessibility"))
                blob.Accessibility = await http.ReadIntAsync("accessibility") ?? blob.Accessibility;

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapPut("/api/images/v1/modifyaccessibility", async (
            HttpContext http, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var names = (await http.ReadValuesAsync("name", "imageName")).Where(BlobStore.IsValidName).ToList();
            var accessibility = await http.ReadIntAsync("accessibility") ?? 1;
            if (names.Count == 0) return Bare.Ok();

            var rows = await db.Blobs.Where(b => names.Contains(b.Name)).ToListAsync(ct);
            foreach (var blob in rows)
            {
                // Someone else's picture stays under their control; an operator can still change it.
                if (blob.OwnerAccountId != caller.Id && !caller.IsAdmin) continue;
                blob.Accessibility = accessibility;
            }

            await db.SaveChangesAsync(ct);
            return Bare.Ok();
        });

        app.MapDelete("/api/images/v1/{name}", async (
            string name, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct) =>
        {
            var caller = await current.RequireAsync(ct);

            var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Name == name, ct);
            if (blob is null) return Bare.Ok();
            if (blob.OwnerAccountId != caller.Id && !caller.IsAdmin) return Results.Forbid();

            db.Blobs.Remove(blob);
            await db.SaveChangesAsync(ct);

            // The row goes first. A file with no row is invisible garbage that a later upload of the
            // same name would silently reuse, whereas a row with no file 404s predictably.
            var path = blobs.PathFor(name);
            if (File.Exists(path)) File.Delete(path);

            return Bare.Ok();
        });
    }

    private static async Task<IResult> ServeAsync(
        string name, BlobStore blobs, RecEmuDb db, CurrentPlayerAccessor current, CancellationToken ct)
    {
        if (!BlobStore.IsValidName(name)) return Results.NotFound();

        var blob = await db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.Name == name, ct);

        // A missing blob row and a row without bytes are the same answer to the client — there is no
        // content — but they are logged differently, because only the second means something deleted
        // the file behind the server's back.
        if (blob is null || !blobs.Exists(name)) return Results.NotFound();

        var viewer = await current.GetAsync(ct);
        if (!BlobStore.CanRead(blob, viewer?.Id, viewer?.IsAdmin == true)) return Results.Forbid();

        return Results.File(blobs.OpenRead(name), blob.ContentType, name);
    }
}