# 29. Darks Games — accounts and social

Namespaces: `SexyBiscuit.Engine.DarksGames` · `html5/src/dg/`

Identity, friends, rich presence, invites, parties, cloud saves and achievements,
from the hub at `darksgames.app`. A published game gets them by carrying a
**catalogue slug**; a game without one loads nothing from the hub.

> Everything here degrades to signed-out. A player who is offline, blocked from
> the hub, or simply not signed in has to be able to play, so no call throws into
> a boot path — `signedIn` is false and every call returns a benign value.

```
Browser                                  Native
DarksGames  ── the hub's own SDKs        DarksGamesRuntime  ── the game-loop seam
  dg-account.v1.js  tokens, saves          DarksGamesAccount  the player's routes
  dg-overlay.v1.js  friends panel,         DarksGamesServer   the s2s routes
                    presence, Join         DarksGamesTokenVerifier  who joined
```

---

## Shipping a game with it

Set the slug and export:

```bash
node html5/tools/export.js Games/MyGame --pwa --dg my-game
```

or in `ProjectSettings.json`:

```json
{ "WindowTitle": "My Game", "darksGames": "my-game" }
```

or `DarksGamesSlug` in the build config, which is what `sbengine` reads.

The exported page then carries the two SDK tags **in this order**:

```html
<script src="https://darksgames.app/sdk/dg-account.v1.js" crossorigin="anonymous" defer></script>
<script src="https://darksgames.app/sdk/dg-overlay.v1.js" crossorigin="anonymous" defer></script>
```

The order is not cosmetic. `dg-overlay.v1.js` strips `?dg_party` and `?dg_launch`
out of the URL the moment it executes, so it must run before any game code reads
`location`; with `defer` that means before the module script. The account SDK
precedes it because the overlay asks it for a token. A test on each side pins it.

### Before it will work

Three things live on the hub, not in this repository:

1. an **`apps` row** for the slug, with the right origin;
2. a **catalogue entry** in `social/catalog.json` — `joinUrlPattern`,
   `maxPlayers`, `partyLaunch`, and `presenceOnly: true` if all you want is
   "In *My Game*" on a friends list;
3. **`DG_APP_SECRET`** in the game server's `.env`, if it will unlock
   achievements server-side.

The slug is also the **token audience** the hub enforces. Getting it wrong shows
up as `presence_app_mismatch` on every presence update rather than as a failure
to sign in.

---

## From a script

```js
DG.available      // the SDKs are on the page and answering
DG.signedIn
DG.userId         // "u_…", the id every server-side call takes
DG.userName       // the display name
DG.handle         // "Darko#4821"
DG.displayName    // name, else handle, else "Player" — never null
DG.game           // the slug

DG.presence({ state: 'wave 7', detail: 'Gatehold', joinCode: room, players: 3, max: 4 });
DG.clearPresence();

DG.achievement('first_clear');
DG.achievement('kills', 10);          // counting

DG.loadSave();                        // arrives on the "save" event
DG.saveCloud({ hp: 90, wave: 7 });

DG.on('user',  (signedIn, id, name) => …);
DG.on('save',  (data) => …);          // null when there is none
DG.on('saveConflict', (server) => …); // another device wrote first
DG.on('achievement', (key, ok) => …);
```

Reads are synchronous properties and writes are fire-and-forget, so a cloud save
arrives on an event rather than as a return value. That is not a browser
limitation: Jint cannot await, and one contract has to describe both engines.

---

## Presence

Presence is what puts a friend's row on somebody's list, and a **join code** is
what puts a **Join** button on it.

```js
dg.presence({ state: 'lobby', joinCode: room.code, joinable: seats < max, players, max });
```

| Field | Limit | Meaning |
|---|---|---|
| `state` | 40 chars | The coarse phase: `menu`, `lobby`, `wave 7` |
| `detail` | 80 chars | Free text: `"Gatehold · Wave 3"` |
| `joinCode` | 64 chars | Makes the presence joinable |
| `joinable` | bool | `false` = "in a room", no Join button |
| `players` / `max` | ints | The seat count on the row |

Two rules the engine applies for you:

- **Codes go out upper-cased.** They are compared case-sensitively after
  extraction, and the hub upper-cases a path-shaped join URL — a lower-case code
  gives a Join button that resolves to a room nobody is in.
- **An identical update is not re-sent.** An idle lobby publishes nothing rather
  than the same line forever.

A game with a running session gets presence with no code at all: `DarksGames`
watches the `NetworkManager` and publishes the room as it changes. Call
`presence()` yourself when you want better words than "in a room".

---

## Join, invites and Party Launch

```js
await dg.init({
    game: 'my-game',
    onJoin: (code) => { joinRoom(code); return true; },   // true = joined in place
});
```

`onJoin` fires when a friend's **Join** button is pressed, an invite is accepted,
or a party room appears. Return `true` when you joined without reloading; return
`false` and the overlay navigates to the join URL instead, which re-runs whatever
deep-link path the game already has. The export wires a default that does exactly
that, so a game overrides `window.sbJoinRoom` only if it can do better.

**Party Launch** works without any state machine in the game: the leader picks the
game, every member's page navigates to it, and the host's `party.arrived` handler
opens a room and reports it — which the engine does, so members' `onJoin` fires
with the code.

---

## Cloud saves

```js
const cloud = await dg.loadSave();       // { data, version, checksum, updatedAt } | null
await dg.writeSave(state, { version: 2, baseUpdatedAt: cloud?.updatedAt });

await dg.syncSave({ read: () => local, write: (s) => apply(s), onConflict: pick });
```

Passing `baseUpdatedAt` is what turns a lost update into a conflict the game can
resolve. Without it, last write wins — which silently loses whichever device
saved first, and is the bug players describe as *"it deleted my progress"*. A
conflict arrives as a `saveConflict` event carrying the server's copy.

---

## Achievements: two paths, and the difference matters

```js
DG.achievement('first_clear');                  // client: a claim
```

```csharp
await server.UnlockAsync(userId, "first_clear"); // s2s: a fact
```

A client report is **self-reported**, accepted only for games the catalogue marks
`clientAchievements: true`, rate-limited and audited. Never hang an entitlement or
a ranking off one. Anything that gates a ranking comes from a server holding
`DG_APP_SECRET`:

```csharp
var server = new DarksGamesServer("my-game");     // reads DG_APP_SECRET

await server.RegisterAchievementsAsync(new[]      // on boot; it upserts
{
    new DarksGamesAchievement("first_clear", "First clear", "Win a run"),
    new DarksGamesAchievement("kills_1000", "Exterminator", points: 50, target: 1000),
});

await server.UnlockAsync(userId, "first_clear");
await server.ProgressAsync(userId, "kills_1000", increment: kills);
await server.PublishPresenceAsync(userId, "wave 7", joinCode: room.Code);
await server.MilestoneAsync(userId, "Cleared wave 20");
```

Without a secret every one of those is a **silent no-op**, so a development box
and a staging copy both run.

---

## Knowing who joined

A client sends its token in the hello; the server verifies it against the hub's
JWKS. Failure means "guest", never "the server is broken".

```csharp
var verifier = new DarksGamesTokenVerifier("my-game");
var identity = await verifier.VerifyAsync(hello.Token);   // null = guest
if (identity != null) player.AccountId = identity.UserId;
```

**A party launch token is not a player identity.** It is signed by the same key,
with the same issuer and the same audience, and carries `typ: "dg-party"`. The
verifier refuses any token with a `typ` claim; without that check a launch token
would sign in whoever holds it. The generated `dgVerify.js` makes the same check.

---

## Closed testing

```bash
node html5/tools/export.js Games/MyGame --pwa --dg my-game --gated
```

This writes a host as well as the build:

```
public/index.html         the gate: account SDK → token → POST /api/session
game/                     the build
server.js                 serves /play/* only for a valid session cookie
server/auth/dgVerify.js   the JWKS verifier, audience already set
server/social.js          the s2s client
DEPLOY.txt                the rest, which happens on the server
```

**The layout is the mechanism, not tidiness.** nginx's
`try_files $uri $uri/index.html @node` serves anything under `public/` straight
off disk without touching Node — a build left there would be ungated and the lock
decorative. `/play/*` exists nowhere on disk, so every request for it falls
through to the host, which is where the session is checked.

And the listing flag on the catalogue (`playtest: true`) hides the tile, the
detail page and the sitemap entry. **That is a listing, not a lock.** Ship both
or you have shipped neither.

---

## Next

- [15. Networking](15-networking.md) — rooms, join codes and the wire
- [18. Build and Export](18-build-export.md) — publishing native builds
- [11. Scripting](11-scripting.md) — the `DG` global
