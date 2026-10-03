# Deployment

Two things have to be reachable by name: the RecNet API, and Photon. The client resolves the API
through DNS for a whole family of subdomains, and connects to Photon at a fixed host and port.

## 1. Name resolution

Rec Room's service-discovery document advertises ~36 subdomains and the client calls them literally.
`deploy/hosts-rec.local` lists them all and mirrors `ServiceCatalog.Labels` in the server exactly.
Copy it into `C:\Windows\System32\drivers\etc\hosts` as administrator, or point each name at your
own host.

Adding a subdomain means editing both that file and `ServiceCatalog.Labels`. Editing only the server
means the client advertises a hostname that does not resolve.

## 2. Server

### Container

    cd deploy
    docker compose up -d recemu

State lives in `deploy/var`, which holds the SQLite database, the JWT signing key, and uploaded
blobs. Back up that directory; it is the server's entire state.

One replica only. SQLite is a single file with a single writer, so two containers would each open
their own copy and diverge silently. Scaling past a handful of players means moving to a server
database first.

### Direct

    dotnet run --project src/RecEmu.Server

Relative paths in `appsettings.json` resolve against the content root, not the process working
directory, so the state directory is the same either way.

## 3. TLS

The client expects `https`. Plain HTTP works for API testing and nothing else.

`deploy/Caddyfile` terminates TLS for `rec.local` and every subdomain in one block, which is what a
wildcard certificate covers. Two settings in it are load-bearing:

- `flush_interval -1` on the reverse proxy. Without it Caddy buffers responses and SignalR
  notifications arrive in bursts instead of as sent, which shows up in-game as presence and room
  state that lags and occasionally desyncs. The symptom does not point at the proxy.
- `request_body max_size 100MB`. Avatar uploads and room bundles exceed the small defaults.

### Certificates

For a real domain, Caddy handles issuance via the `tls` profile:

    docker compose --profile tls up -d certbot

For a local setup, generate a certificate and trust it on the machine running the game. Trusting the
certificate is more reliable than the mod's `SkipCertificateValidation` flag, which only affects
.NET-handled requests and not Photon's native TLS stack.

## 4. Photon

Photon's C++ load balancer is a licensed binary from the Photon documentation portal, so it is not
included here. `deploy/photon.config.template` documents each setting and why it matters.

Three values have to agree across three files, and a mismatch produces a client that connects to the
API fine and then cannot join a room:

| Value | Server | Photon | Client mod |
|---|---|---|---|
| App id, realtime | `appsettings.json` | `ApplicationIdRealtime` | `AppIdRealtime` |
| App id, voice | `appsettings.json` | `ApplicationIdVoice` | `AppIdVoice` |
| Host and port | `appsettings.json` | `Address` / `Port` | `PhotonServer` / `PhotonPort` |

Defaults, if you have no reason to change them:

- Realtime (PUN) app id `ce298166-7e52-4c47-a1c2-0ce4b7a7cfcb`
- Voice app id `5fa7359e-b671-4910-8226-cb539cdae485`
- Port `5000`

The region string needs one more piece of agreement. The mod sets the client's fixed region to
`localhost`, and Photon must have a region literally named `localhost` configured as its default
master. A mismatch means the client starts a connection, never finds the master server, and times out
with no useful error.

## 5. Client mod

    set MelonLoaderDir=C:\path\to\MelonLoader
    dotnet build client\RecEmu.ClientMod\RecEmu.ClientMod.csproj -c Release

Copy `RecEmu.ClientMod.dll` into the game's `Mods` folder. The mod writes `RecEmuRedirect.cfg` beside
itself on first launch; edit and restart to adjust.

The mod logs what it patched and what it could not find. If Photon does not redirect, that log says
whether the game method it patches is present, which is the first thing to check after a game update
renames it.

## Troubleshooting

**Client never contacts the server.** Name resolution. Confirm with `nslookup api.rec.local`; a
public DNS answer or NXDOMAIN means the hosts entry is missing.

**API works, rooms will not load.** Photon. Check the Photon log for a rejected client, then compare
the three-way table above.

**Presence and room state lag, or desync.** `flush_interval -1` missing from the Caddyfile.

**Everything 401s after a restart.** The signing key moved or was regenerated. It lives at
`src/RecEmu.Server/var/signing.key` and is created on first run. Deleting it invalidates every
issued token, which is exactly what it looks like.

**Graphical rooms are blank.** Expected. The seeded Dorm blob is a valid structure with no scene
content.