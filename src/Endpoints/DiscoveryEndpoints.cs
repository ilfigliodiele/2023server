using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Infrastructure;

namespace RecEmu.Server.Endpoints;

/// <summary>
/// Service discovery.
///
/// The client fetches this document before anything else and builds its whole route table from it:
/// label to base URL. Getting it wrong breaks exactly the subsystems behind the bad labels, and
/// does so silently, because the client falls back to its compiled-in defaults — which point at
/// the real Rec Room. So the document is served on the apex and on the ns host, and it is the one
/// response worth eyeballing by hand after any domain change.
/// </summary>
public static class DiscoveryEndpoints
{
    public static void Map(WebApplication app)
    {
        // Both "/" on the nameserver hosts and the explicit path, because which of the two the
        // client asks for depends on the service map it built.
        app.MapGet("/", (HttpContext context, RecEmuOptionsAccessor options) =>
        {
            var subdomain = ServiceCatalog.SubdomainOf(context.Request.Host.Host, options.Value);
            if (subdomain is not null && !ServiceCatalog.NameserverHosts.Contains(subdomain, StringComparer.OrdinalIgnoreCase))
                return Results.NotFound();

            return Results.Json(ServiceCatalog.BuildEndpoints(options.Value), Json.Options);
        });

        app.MapGet("/ns", (RecEmuOptionsAccessor options) =>
            Results.Json(ServiceCatalog.BuildEndpoints(options.Value), Json.Options));

        app.MapGet("/ns/endpoints", (RecEmuOptionsAccessor options) =>
            Results.Json(ServiceCatalog.BuildEndpoints(options.Value), Json.Options));

        app.MapGet("/api/config/v1/service-map", (RecEmuOptionsAccessor options) =>
            Results.Json(ServiceCatalog.BuildEndpoints(options.Value), Json.Options));

        app.MapGet("/healthz", () => Results.Text("ok"));
    }
}
