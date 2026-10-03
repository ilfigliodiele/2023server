# RecEmu.ClientMod

MelonLoader mod that points Rec Room at a private server.

## Why not BepInEx

The previous patch for this was BepInEx with Harmony attributes naming `PhotonNetwork`,
`PhotonVoiceNetwork`, and `LoadBalancingClient` directly. That cannot work: Rec Room is an IL2CPP
build, so those types do not exist as usable compile-time references, and there is nothing to name in
a `[HarmonyPatch]` attribute.

This mod uses MelonLoader instead, and resolves every game type by name at runtime through reflection.
Nothing in the source references a game type at all.

## Design consequence: never throw

Because targets are resolved by string, they can be missing. A game update that renames
`ConnectUsingSettings` should cost you the Photon redirect and log a line saying so, not take the game
down at startup with a `TypeLoadException`. Every lookup failure is logged and skipped.

This is why the mod has no compile-time dependency on the game: it can be built without it, and it can
survive the game changing.

## Build

MelonLoader 0.6 ships as a release zip, not a NuGet package, so the csproj takes a path:

    set MelonLoaderDir=C:\path\to\extracted\MelonLoader
    dotnet build client\RecEmu.ClientMod\RecEmu.ClientMod.csproj -c Release

`MelonLoaderDir` must contain `MelonLoader.dll` and `0Harmony.dll`. Set it in `Directory.Build.props`
or at the command line; the project errors out with a clear message if it is wrong, rather than
producing a wall of unresolved-reference errors.

References are compile-only and `Private=false`. The game folder already has these assemblies, and
shipping a second copy causes two MelonLoaders in one process.

Output is a single `RecEmu.ClientMod.dll`. Copy it into the game's `Mods` folder.

## Configuration

`RecEmuRedirect.cfg` is written next to the DLL on first launch. Edit it and restart the game.

| Key | Default | Meaning |
|---|---|---|
| `BaseUrl` | `https://www.rec.local` | API base. Only used if `UrlMembers` is set; normally the hosts file handles this. |
| `PhotonServer` | `127.0.0.1` | Photon host |
| `PhotonPort` | `5000` | Photon port |
| `PhotonUseWss` | `true` | Use `wss`/`ws` |
| `PhotonRegion` | `localhost` | Must match a region in the Photon config |
| `AppIdRealtime` | see `appsettings.json` | Realtime app id |
| `AppIdVoice` | see `appsettings.json` | Voice app id |
| `SkipCertificateValidation` | `true` | Development only. Does not affect Photon's native TLS. |
| `UrlMembers` | empty | `Type::Member` pairs to redirect in-client, comma separated |

## What it patches

- `PhotonNetwork.ConnectUsingSettings` — rewrites the app settings object (server, port, protocol,
  both app ids, region) before PUN connects.
- `PhotonVoiceNetwork.Connect` — rewrites the voice client's own app id and address. Separate client
  object, so the settings rewrite above does not cover it.
- `LoadBalancingClient.ConnectToNameServer` — returns false to skip the call. A self-hosted load
  balancer answers neither name nor master lookup on that path, so it hangs until timeout.
- Anything named in `UrlMembers` — string getters replaced with `BaseUrl`.

Settings are patched in memory rather than by editing the game's `PhotonServerSettings` asset on disk,
which would mean repacking resource bundles.

## When a game update breaks it

Check the MelonLoader log. It lists each patch applied and, for each target it could not find, the
name it looked for. A rename shows up there directly. Update the candidate name lists in `Mod.cs` and
rebuild; no other change is needed.

`MelonGame` in `AssemblyInfo.cs` also has to match. If the mod stops loading entirely, that pair is
the reason; MelonLoader prints the name and developer it detected.