# Client-visible contract notes

The Rec Room client is an IL2CPP Unity build with its own serializers, and several of its expectations
are not guessable from the route list. These are the ones that cost time.

## Response casing

Client DTOs accept PascalCase, camelCase, and lowercase. They do not agree on the spelling of every
field: `UserName` and `Username` are both seen, in different families. Serialization therefore
emits explicit keys rather than relying on a naming policy.

Responses also carry aliases, so one logical field appears under several spellings. This is
deliberate for client compatibility and has one practical consequence: PowerShell's `ConvertFrom-Json`
rejects a document with duplicate keys and throws. That is a PowerShell limitation, not a server
error. Smoke scripts extract the access token with a regex instead.

## Scalar versus object bodies

Endpoints that return a bare value must return the bare JSON value, not a wrapper:

    GET /account/me/stats/rooms.built   ->   0
    GET /www/v1/motd                   ->   "Welcome to RecEmu."
    GET /healthz                       ->   ok

Wrapping these in `{"value": ...}` deserializes to null in the client with no error.

## Mutations return full details

Any room mutation responds with the complete room object, including `SubRooms`, `Roles`, `Tags`, and
`Stats` — not an acknowledgement and not a partial patch. The client replaces its local copy with the
response, so a partial response drops the collections it does not mention.

Routes are mounted under both the bare path and the `roomserver/` prefix, because different client
builds call different ones.

## Request parsing

The same logical parameter arrives in query strings, form bodies, multipart bodies, as repeated
values, and as JSON, depending on the call site. `RequestValues` reads all of them, including
repeated keys. A handler that reads only `Request.Query` silently misses the majority of real
requests.

## Service hosts

The client fetches a service-discovery document and then routes every later call by the label in it.
Consequences:

- A wrong label breaks exactly one subsystem, silently, because the client falls back to its own
  default URL.
- The document advertises ~36 subdomains, so name resolution has to cover all of them. One missing
  hosts entry is one broken feature, not a startup error.
- Most routes are served on every host on purpose. A handful genuinely differ per service host, and
  for the rest the client's host assignment is only inferable from the binary, not provable.

## SignalR

- Path `/hub/v1`.
- Client receives `Notification(string)`.
- Server sends an envelope: `{"Id": <int>, "Msg": <string>}`.
- Push ids in use: `11` AccountUpdate, `12` PresenceUpdate, `13` RoomInstanceUpdate, `15` RoomUpdate,
  `90` ChatMessageReceived, `95` CommunityBoardUpdate.

The id is serialized as a number even when the internal value is a string, and the client rejects the
envelope otherwise.

## Matchmaking and the Dorm

`/matchmake/dorm` returns a room instance, not a room id. It must work on a fresh install and after an
operator renames the lobby, so the Dorm is resolved by id, recorded in the `dorm.roomId` server
setting on first seed. Name lookup is the migration path for databases seeded before that setting
existed, and the oldest public room is the last resort.

Room instances are joined by id through `/matchmake/instance/{id}`, which returns the same instance
shape.

## Versioning

Default reported version is `20230317`, manifest `7859140924511540835`. A client that reports a
different build needs its own value; the check is the client's, and the server does not enforce one.

## Not covered

The upstream contract lists roughly 518 route patterns. The families above are implemented and
smoke-tested; not every individual route has been verified end to end.