# RecEmu

A Rec Room 2023-compatible private server, written in C# on .NET 10, plus a MelonLoader client mod
that points the game at it.

This is an independent implementation written against the public Rec Room API surface. No code was
taken from Rec Room or from existing private-server projects; some existing projects are licensed
under terms that make reuse awkward, and this one starts from observed request and response shapes
instead.

## What works

- Full RecNet HTTP surface: accounts, auth, rooms, matchmaking, clubs, moderation, progression,
  store, stats, social, studio, and the rest of the route families, served under the service-host
  conventions the client expects.
- SignalR push at `/hub/v1` using the contract's envelope, including the account, presence, room
  instance, room, chat, and community-board push ids.
- SQLite persistence with a seeded Dorm and Orientation room.
- Photon integration for realtime and voice, pointed at by the client mod.

## What does not, yet

- **Room graphics.** The Dorm's data blob is structurally valid but is not a real scene, so a client
  spawns into an empty grey void. Populating it needs official assets or the room editor.
- **Voice and Photon deployment.** Photon's C++ server is a licensed binary and is not included.
  `deploy/photon.config.template` documents what has to be filled in.
- **Full contract coverage.** The routes listed above are implemented and smoke-tested. The upstream
  contract has roughly 518 route patterns and not all of them have been individually verified.

## Layout

    src/RecEmu.Server/     the server
    client/RecEmu.ClientMod/   MelonLoader mod for the game
    deploy/                container, reverse proxy, hosts, Photon template
    docs/                  deployment and contract notes

## Running the server

    dotnet run --project src/RecEmu.Server

Listens on `http://localhost:8080`. Configuration is in `src/RecEmu.Server/appsettings.json`.

Relative paths in that file are resolved against the content root, not the process working
directory, so the database, signing key, and blob store all land under `src/RecEmu.Server/var/` no
matter where the process was launched from.

That directory is called `var` rather than the conventional `data` because the server already has a
`Data` source folder, and Windows matches directory names case-insensitively. A runtime path of `data`
inside the project is the same folder as `Data/Entities.cs`, so the database ends up in the source
tree.

On first run the database is created and seeded with a Dorm and an Orientation room. The Dorm's id is
recorded in the `dorm.roomId` server setting; `/matchmake/dorm` resolves through that setting, so
renaming the lobby does not break matchmaking.

## Pointing the game at it

Two pieces, both required.

### 1. Name resolution

The client resolves `rec.local` and its subdomains through DNS, so they have to point at the server.
Edit `C:\Windows\System32\drivers\etc\hosts` as administrator:

    deploy\hosts-rec.local

The file lists the apex plus every subdomain the service-discovery document advertises. A missing
entry shows up as one broken feature, not a startup error.

### 2. The client mod

Rec Room ships as IL2CPP, which means no compile-time references to game types are available to a mod
and nothing can be patched by type reference. `client/RecEmu.ClientMod` resolves everything it needs
by name at runtime, and logs what it found and what it skipped.

Build it against an extracted MelonLoader 0.6:

    set MelonLoaderDir=C:\path\to\MelonLoader
    dotnet build client\RecEmu.ClientMod\RecEmu.ClientMod.csproj

Then drop `RecEmu.ClientMod.dll` into the game's `Mods` folder. On first launch the mod writes
`RecEmuRedirect.cfg` next to itself; edit that file and restart the game to change hosts, ports, app
ids, and certificate behaviour.

MelonLoader 0.6 ships as a release zip rather than a NuGet package, which is why the csproj takes a
path instead of a `PackageReference`.

## Deployment

See [docs/deployment.md](docs/deployment.md) for the container and reverse-proxy setup.

## Contract notes

[docs/contract-notes.md](docs/contract-notes.md) records the client-visible behaviours that are easy
to get wrong: response casing, scalar-versus-object bodies, the full-details-after-mutation rule, and
the request-parsing variants.