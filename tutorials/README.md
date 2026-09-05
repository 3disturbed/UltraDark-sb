# SexyBiscuit Engine — Tutorials

A guided path from an empty window to a shipped game. Every tutorial builds a
working thing, and every code block was written against the actual engine source
rather than the design document.

> **Verified against commit `407d32c`.** The engine is under active development.
> If a snippet disagrees with the code, the code is right — the
> [gotchas page](../wiki/21-gotchas.md) carries a one-line check for every claim
> that would cost you time.

For reference material — "what does this class do" — see the
[wiki](../wiki/README.md).

---

## How to use these

Each tutorial is self-contained but they assume you have done the earlier ones.
Work through 1–3 in order; after that, take what you need.

**Tutorials 1–3 are not optional.** They establish the bootstrap pattern that
every later tutorial reuses, including the parts of the frame your game has to
drive itself.

Everything is built in one project, `MyGame`, created in
[Tutorial 1](01-hello-window.md).

---

## Part I — Foundations

| # | Tutorial | You will build | Time |
|---|---|---|---|
| 1 | [Hello, Window](01-hello-window.md) | A project, a window, the engine bootstrap | 20 min |
| 2 | [Sprites & Cameras](02-sprites-and-cameras.md) | A visible world with a following camera | 25 min |
| 3 | [Input & Movement](03-input-and-movement.md) | A character you can drive on keyboard and gamepad | 25 min |
| 4 | [Writing Components](04-components.md) | Health, damage, pickups — your own components | 30 min |

## Part II — Making it a game

| # | Tutorial | You will build | Time |
|---|---|---|---|
| 5 | [JavaScript Scripting](05-javascript-scripting.md) | Enemy behaviour in hot-reloadable `.js` | 30 min |
| 6 | [A Physics Platformer](06-physics-platformer.md) | Gravity, jumping, tilemap collision | 45 min |
| 7 | [UI & Menus](07-ui-and-menus.md) | Main menu, HUD, pause screen | 40 min |
| 8 | [Audio](08-audio.md) | Music, SFX, buses, a volume menu | 25 min |
| 9 | [Animation & Tweens](09-animation-and-tweens.md) | Sprite animation, juice, screen shake | 35 min |

## Part III — Structure and content

| # | Tutorial | You will build | Time |
|---|---|---|---|
| 10 | [Scenes & Prefabs](10-scenes-and-prefabs.md) | Multiple levels, spawn factories, pooling | 35 min |
| 11 | [Saving & Loading](11-saving-and-loading.md) | Save slots, settings, a per-user data directory | 30 min |
| 12 | [Particles & Post-FX](12-particles-and-postfx.md) | Explosions, trails, 2D lighting | 30 min |
| 13 | [3D Basics](13-3d-basics.md) | A lit 3D scene with an orbit camera | 40 min |

## Part IV — Systems

| # | Tutorial | You will build | Time |
|---|---|---|---|
| 14 | [The Gameplay Framework](14-gameplay-framework.md) | `GameMode`, players, respawns, a scoreboard | 45 min |
| 15 | [AI](15-ai.md) | Patrolling, seeing, chasing enemies | 45 min |
| 16 | [Multiplayer](16-multiplayer.md) | Host/join, replication, RPCs | 60 min |

## Part V — Shipping

| # | Tutorial | You will build | Time |
|---|---|---|---|
| 17 | [Editor Workflow](17-editor-workflow.md) | Using — and working around — the editor | 20 min |
| 18 | [Shipping Your Game](18-shipping.md) | A packaged, distributable build | 40 min |
| 20 | [Building a Game with Claude](20-building-a-game-with-claude.md) | A level and a C# component built by talking to Claude in the editor | 30 min |

## Capstone

| # | Tutorial | You will build |
|---|---|---|
| 19 | [Build a Complete Game](19-capstone-twin-stick.md) | **Biscuit Blaster** — a finished twin-stick shooter, start to finish |

---

## Before you start

You need:

- The .NET 8 SDK (`dotnet --list-sdks`)
- A text editor — VS Code, Rider or Visual Studio
- This repository, building cleanly:

```bash
dotnet build SexyBiscuit.sln
```

You do **not** need the editor for any of these — every tutorial is code-first,
which is how the demo project works too. The editor is cross-platform and
genuinely useful for laying scenes out; [Tutorial 17](17-editor-workflow.md)
covers where each approach fits.

## The one thing to know up front

`SBEngine.Draw` walks the scene without opening a `SpriteBatch`, so a game that
does not override `Draw` renders **nothing** — not a black screen with a
warning, an exception. Tutorial 1 sets up the bootstrap that fixes it, and every
later tutorial builds on it.

The engine drives time, input, physics, the scene graph, tweens, timers,
coroutines, audio and the 3D render pass. Your game drives the 2D sprite batch,
networking, script hot reload and the debug tooling. Full breakdown on
[wiki page 3](../wiki/03-game-loop.md).
