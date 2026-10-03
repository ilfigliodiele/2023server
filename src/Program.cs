using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RecEmu.Server.Auth;
using RecEmu.Server.Configuration;
using RecEmu.Server.Data;
using RecEmu.Server.Endpoints;
using RecEmu.Server.Infrastructure;
using RecEmu.Server.Matchmaking;
using RecEmu.Server.Realtime;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
//
// Options are bound once into a plain object and then handed around as
// RecEmuOptionsAccessor rather than as IOptions<T>. Every consumer in this
// codebase wants the values synchronously at construction time — a hosted
// service, a hub, a singleton push dispatcher — and threading IOptionsMonitor
// through all of them buys nothing, because nothing reloads these at runtime.
// ---------------------------------------------------------------------------
var options = builder.Configuration.GetSection(RecEmuOptions.SectionName).Get<RecEmuOptions>()
              ?? new RecEmuOptions();

// Relative paths are resolved against the content root, not the process working directory. The
// difference decides whether the database, the signing key and the blob store land in the project
// folder or next to whatever shell happened to launch the process, and the second case silently
// starts the server on an empty database with a new signing key — which invalidates every live
// session and looks like a client bug.
var contentRoot = builder.Environment.ContentRootPath;

static string Anchor(string root, string path)
    => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.Combine(root, path);

options.SecretFile = Anchor(contentRoot, options.SecretFile);
if (!string.IsNullOrWhiteSpace(options.BlobRoot)) options.BlobRoot = Anchor(contentRoot, options.BlobRoot);

// Blob storage defaults to a folder under the content root so an operator can
// drop files in without configuring anything.
//
// The "var" directory rather than the conventional "data" one, because this project already has a
// source folder named Data and Windows matches directory names case-insensitively: a runtime path of
// data/blobs inside the project is the same folder as Data/Entities.cs. Files land in the source
// tree, .gitignore rules for the source folder do not cover them, and a clean looks like a bug.
if (string.IsNullOrWhiteSpace(options.BlobRoot))
    options.BlobRoot = Path.Combine(contentRoot, "var", "blobs");

builder.Services.AddSingleton(new RecEmuOptionsAccessor(options));

// ---------------------------------------------------------------------------
// Persistence
//
// EnsureCreated rather than migrations: the schema comes from OnModelCreating,
// and a migration history that has to be kept in step with a model nobody has
// shipped yet only creates a third thing that can drift out of sync.
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("RecEmu")
                       ?? $"Data Source={Path.Combine(contentRoot, "var", "recemu.db")}";

// A bare relative "data/recemu.db" has the same working-directory problem as above, so rewrite it
// before it reaches SQLite rather than after the first request.
var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
if (!string.IsNullOrWhiteSpace(dataSource))
    connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString)
        { DataSource = Anchor(contentRoot, dataSource) }.ToString();

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(
    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource))!);

// Split queries rather than a single joined one: room details load SubRooms, Roles and Bans, and
// a single query over three collection navigations returns the cross product of all three. Splitting
// issues one query per collection, which is cheaper than the join and avoids EF's
// multiple-collection-include warning entirely.
builder.Services.AddDbContext<RecEmuDb>(db => db.UseSqlite(
    connectionString,
    sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSignalR(signalr => signalr.EnableDetailedErrors = builder.Environment.IsDevelopment());

// ---------------------------------------------------------------------------
// Application services
//
// Lifetimes follow what the state actually is, not what is convenient. Presence
// and occupancy are per-process session state, so singletons; anything holding a
// DbContext is scoped, because a DbContext is not thread-safe and outliving a
// request is how entity leaks happen.
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<CurrentPlayerAccessor>();
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddSingleton<OccupancyTracker>();
builder.Services.AddScoped<RoomInstanceService>();
builder.Services.AddScoped<MatchmakingEndpoints.Session>();
builder.Services.AddScoped<StatRecordService>();

builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton<IPushSender, SignalRPushSender>();
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddHostedService<InstanceReaper>();

// ---------------------------------------------------------------------------
// Authentication
//
// The client sends "Bearer <jwt>" on HTTP routes and the same token in the
// access_token query string on the SignalR handshake, because the game's
// WebSocket client cannot attach an Authorization header to the upgrade request.
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(jwt =>
    {
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.JwtIssuer,
            ValidateAudience = true,
            ValidAudience = options.JwtIssuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(SigningKey.Resolve(options)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        jwt.MapInboundClaims = false;
        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token) &&
                    context.Request.Query.TryGetValue("access_token", out var fromQuery))
                {
                    context.Token = fromQuery;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// The editor uploads whole room scenes and avatar bundles; Kestrel's 30 MB
// default is below what a real Rec Room scene weighs.
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = 256L * 1024 * 1024;
    form.ValueLengthLimit = 16 * 1024 * 1024;
    form.MemoryBufferThreshold = 4 * 1024 * 1024;
});

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNameCaseInsensitive = true;
    json.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Schema and first-boot content
// ---------------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RecEmuDb>();
    await db.Database.EnsureCreatedAsync();
    await Seed.ApplyAsync(db, scope.ServiceProvider.GetRequiredService<RecEmuOptionsAccessor>(), CancellationToken.None);
}

// ---------------------------------------------------------------------------
// Duplicate route check
//
// Routes are declared by hand across a dozen endpoint classes, and two classes claiming the
// same path and verb produces an AmbiguousMatchException — a 500 on whichever unlucky request
// happens to hit it, with nothing in the client to explain it. Enumerating the built table here
// turns that into a startup warning.
// ---------------------------------------------------------------------------
foreach (var duplicate in app.Services.GetRequiredService<EndpointDataSource>().Endpoints
             .OfType<RouteEndpoint>()
             .SelectMany(route => (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                                      ?? ["ANY"])
                 .Select(verb => (Key: $"{verb} {route.RoutePattern.RawText}", route.DisplayName)))
             .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
             .Where(group => group.Count() > 1)
             .Select(group => $"{group.Key} -> {string.Join(", ", group.Select(e => e.DisplayName))}"))
{
    app.Logger.LogWarning("duplicate route registration: {Route}", duplicate);
}

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
app.UseRouting();

// One host allowlist for every service. The client addresses each service by a
// distinct subdomain, so a request on a host that is not in the advertisement is
// either a misconfiguration or a probe; serving a real route for it hides both.
app.Use(async (context, next) =>
{
    var current = context.RequestServices.GetRequiredService<RecEmuOptionsAccessor>();
    if (!current.Value.ServeAllHosts)
    {
        var host = context.Request.Host.Host;
        var known = ServiceCatalog.AllHosts(current.Value).Contains(host) ||
                    host.Equals(current.Value.Domain, StringComparison.OrdinalIgnoreCase);
        if (!known)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }

    // Several routes read a JSON body after model binding has already run, and
    // the subroom-data route parses the body itself, so it must be seekable.
    context.EnableBodyBuffering();
    await next();
});

app.UseWebSockets();

// UnauthorizedException is CurrentPlayerAccessor's "no usable caller" signal.
// Unhandled it becomes a 500, and the client reports a server fault for what is
// really an expired session.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<NotifyHub>("/hub/v1");

DiscoveryEndpoints.Map(app);
AuthEndpoints.Map(app);
AccountEndpoints.Map(app);
SocialEndpoints.Map(app);
RoomEndpoints.Map(app);
MatchmakingEndpoints.Map(app);
ClubEndpoints.Map(app);
ChatEndpoints.Map(app);
StoreEndpoints.Map(app);
ImageEndpoints.Map(app);
StorageEndpoints.Map(app);
LeaderboardEndpoints.Map(app);
PlayerSettingsEndpoints.Map(app);
ModerationEndpoints.Map(app);
ProgressionEndpoints.Map(app);
StudioEndpoints.Map(app);
MiscEndpoints.Map(app);

await app.RunAsync();

/// <summary>Exposed so tests and tooling can reference the entry-point assembly.</summary>
public partial class Program;