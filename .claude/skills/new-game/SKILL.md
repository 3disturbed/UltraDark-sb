---
name: new-game
description: Start a browser-playable game from a template in a game repo seeded from the engine, validate it, and hand the team a play URL or an installable web build. Use for "new game", "start a game called X", "make a platformer prototype".
---

# New game

Arguments: `<repo-url> <template> <name> [brief...]`. The template is one of the folders under
`Templates/` (`2D Platformer`, `Twin-Stick Shooter`, `Tower Defense`, `Puzzle Match3`,
`Visual Novel`, `Racing`, `Endless Runner`, `Top-Down RPG`, `Survival Crafting`,
`Fighting Game`, `3D Scene`, `Hello World`, `Empty`, `Multiplayer`, `Multiplayer Arena`).
Pick the closest one to the brief when the user does not name one.

## Steps

1. **Seed the repo.** If the current checkout is the engine repo, clone it beside itself and
   point it at the game repo; the engine stays reachable as `upstream` so fixes can be merged:

   ```bash
   git clone <engine-repo-or-this-checkout> <name> && cd <name>
   git remote rename origin upstream
   git remote add origin <repo-url>
   ```

   If the current checkout is already the game repo, skip this.

2. **Create the game** under `Games/<name>/` from the template. Copy the folder, then set
   `WindowTitle` in `Games/<name>/ProjectSettings.json` to the game's name and check
   `StartScene`. Write the brief into `Games/<name>/BRIEF.md` (one paragraph: the fantasy, the
   core loop, what "fun" would mean for the first playtest).

3. **Write the game.** Edit `Games/<name>/Scripts/*.js` and `Scenes/*.scene` as text. Use only
   the shared scripting contract (`wiki/11-scripting.md`; the list is
   `html5/src/scripting/bridge-api.json`) and read the template's own scripts for idioms. Do not
   read the engine source to write a game.

4. **Validate** until it prints `OK`, and run the suite once:

   ```bash
   cd html5 && npm run validate -- ../Games/<name> && npm test
   ```

5. **Commit and push** (`git add Games/<name> && git commit -m "<name>: first playable" && git push -u origin main`).

6. **Hand off a build.**

   ```bash
   node html5/tools/export.js Games/<name> --pwa
   ```

   The zip in `Games/<name>/dist/` goes on the server as a Game Card; it is not sent to the
   publish API, which is for native builds. To let the team play it now, serve it:
   `node html5/tools/serve.js --watch` and report the LAN URL it prints, with
   `?project=/Games/<name>/`.

7. **Report in three lines**: what the game is, the URL, and what the team should judge in the
   first playtest.

## Rules that keep the session cheap

- One game per session; open with the brief and `validate`, not with a scene dump.
- No screenshots: the validator and the template smoke tests are the checker.
- A member the validator flags as outside the contract will fail on one engine; fix the
  script, do not widen the bridge for one game.
