# The fleet box — production notes for the machine the games run on

Everything here applies only on the Linux box that hosts DarksGames and the game services. It
is loaded on that box through a gitignored `CLAUDE.local.md` containing `@ops/fleet-box.md`;
the root `CLAUDE.md` says how to recognise the box (`/srv/darksgames` exists). Nothing in this
file applies on a development Mac.

---

## Working on this box

This machine is the one the fleet runs on, and **it is production**. Everything below is already
installed and configured — do not set any of it up again, and do not `apt-get` or
`dotnet workload install` your way around a path you have not checked.

| What | Where |
|---|---|
| .NET SDK | `/usr/lib/dotnet` — export `DOTNET_ROOT=/usr/lib/dotnet` and add it to `PATH` |
| JDK (Android) | OpenJDK 17; `JAVA_HOME=$(dirname $(dirname $(readlink -f $(which java))))` |
| Android SDK | `/opt/android-sdk` — export `ANDROID_HOME=/opt/android-sdk` |
| `android` workload | installed for .NET 10 |
| Engine checkout | this repository; `SEXYBISCUIT_REPO` if a build cannot find it |

Two environment traps that cost real time:

- **`/tmp` is a 3.8 GB tmpfs**, i.e. RAM, and it is not empty. A `dotnet workload install` or a
  large restore through it dies with "No space left on device" while `df /` cheerfully reports
  50 GB free. Export `TMPDIR=/var/tmp/dotnet-workload` for anything that unpacks a lot. The box
  has been OOM-killed before; filling `/tmp` is one of the ways to do it again.
- **An Android build wants all three variables at once.** Missing one gives a different error each
  time — `XA5300` for the SDK, a JDK complaint, or a workload message — so set the three together
  and `AndroidPublisher` will name whichever is missing if you forget.

```bash
export DOTNET_ROOT=/usr/lib/dotnet PATH=$PATH:/usr/lib/dotnet
export JAVA_HOME=$(dirname $(dirname $(readlink -f $(which java))))
export ANDROID_HOME=/opt/android-sdk
export TMPDIR=/var/tmp/dotnet-workload
```

**Build tokens live on this box, one per app slug.** `~/.dg-build-token` is UltraDark's and will
403 for anything else; each game gets its own, e.g. `~/.dg-build-token-jake01`. Mint one at
Admin → Builds.

**Never `pkill -f "node server.js"`.** It matches every game server on the box — about two dozen —
and has caused a full fleet outage. Kill by PID.

**Deploying is local.** There is no staging box and no deploy-over-SSH: the site is prerendered
and rsynced from the checkout into `/srv/darksgames/site`, and a game service is
`systemctl restart darksgame@<slug>`. Read `--delete` dry-runs before trusting them; the live web
root holds files that are not in the repo.


## Build tokens: scope and minting

**A build token is scoped to one app slug.** `build_tokens.app_slug` pins it; a token minted for
another game answers every publish with

```
403 forbidden: This build token may only publish for "ultradark".
```

which is deterministic and will not fix itself on a retry. A new game needs its **own** token,
minted at Admin → Builds; the raw value is shown exactly once and only its SHA-256 is kept, so a
lost token is a re-minted token. Revoke rather than delete, so the audit trail survives.

---

## Phase 3b — closed testing on DarksGames (the HTML5 build)

The publish API above serves **downloadable native builds**. A web build goes on the site as a
**Game Card** instead, and while a prototype is still being judged that card is for playtesters
only. This is the recipe, in the order it has to happen. It runs as root **on the server**, which
is production — there is no staging box.

### The gate is two layers, and only one of them is a lock

`playtest: true` on the catalogue entry hides the tile, the detail page and the sitemap entry from
everyone without the flag. **That is a listing, not a lock** — anyone handed the URL can still open
the game. The lock is the game's own server checking the `playtester` claim on a hub access token.
Build both or you have built neither.

The claim is minted by dg-accounts only when the flag is set, so its absence is a plain "no" and
there is nothing to look up. Revoking takes effect within one access-token lifetime.

### The trap that makes the lock useless

`add-game` writes an nginx vhost whose `try_files $uri $uri/index.html @node` serves **anything
that exists in the game's `public/` straight off disk**, never touching Node. Put the export there
and the gate is decoration.

So the build does not live in `public/`. Layout:

```
/srv/darksgames/games/<slug>/
  public/index.html     the gate: account SDK -> token -> POST /api/session
  game/                 the export from `node html5/tools/export.js <project> --pwa`
  server/auth/dgVerify.js   the JWKS verifier, with the playtester claim
  server/social.js      the s2s client, for achievements and server presence
  server.js             serves /play/* from game/ only for a request carrying the session cookie
  .env                  your session secret; add-game merges PORT= into it
```

`/play/…` exists nowhere on disk under `public/`, so every request for it falls through to Node,
which is the whole point.

**The export writes all of that.** `--gated` produces exactly this layout, with the slug already
filled in and a `DEPLOY.txt` carrying the rsync and the dg-accounts steps, so the recipe below is
now what to check rather than what to type:

```bash
node html5/tools/export.js Games/<Name> --pwa --dg <slug> --gated
```

### The order

```bash

### Things that will bite

- **Re-read the live `sw.js` before bumping.** Another session may have bumped it since you last
  looked; `tools/prerender.mjs` fails on drift between `VERSION` and the `?v=` tags, and
  `link.html` is not covered by that check — sed it by hand.
- **`--delete` on the site rsync.** The live web root can hold files that are not in the repo.
  Dry-run first and read what it wants to remove.
- **A playtest route still needs a prerendered file** — a tester who reloads the detail page meets
  nginx before the SPA, and no file is a real 404. It must not carry the copy, though: the
  prerenderer writes a blank `noindex` shell for a `playtest: true` entry and lets `app.js` render
  the real view once it has checked the claim.
- **certbot serialises.** "Another instance of Certbot is already running" means a renewal held the
  lock; everything else in `add-game` finished, so just re-run
  `certbot --nginx --redirect -d <domain>`.
- **A service worker outlives the session.** The export precaches the whole build, so a tester
  whose cookie lapses can still play offline from cache. Fine for closed testing; not a reason to
  treat the cookie as a licence check.
