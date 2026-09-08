# SexyBiscuit Engine — Wiki

Reference documentation for the SexyBiscuit engine, written against the actual
source in `SexyBiscuit.Engine/`. Where the engine's behaviour differs from the
design document in the repository root [`README.md`](../README.md), **this wiki
describes what the code does today** and says so explicitly.

If you want a guided, build-something-first path instead of reference material,
start at [`../tutorials/`](../tutorials/README.md).

---

## Start here

| Page | What it covers |
|---|---|
| [1. Getting Started](01-getting-started.md) | Prerequisites, solution layout, building, running the demo |
| [2. Core Architecture](02-core-architecture.md) | `SBEngine`, `Scene`, `Layer`, `Actor`, `Component`, `Transform`, lifecycle order |
| [3. The Game Loop](03-game-loop.md) | What the engine pumps for you, what you must pump yourself, and the recommended bootstrap |

> **Read page 3 before you write a game.** `SBEngine.Draw` does not open a
> `SpriteBatch` around the 2D scene pass, so a game that does not override
> `Draw` renders nothing. Page 3 has the bootstrap.

## Systems

| Page | What it covers |
|---|---|
| [4. 2D Rendering](04-rendering-2d.md) | `SpriteRenderer`, `Camera2D`, `RenderSystem2D`, tilemaps, particles, post-processing |
| [5. 3D Rendering](05-rendering-3d.md) | `Transform3D`, `Camera3D`, `MeshRenderer`, `Material3D`, `Light3D`, `Skybox`, LOD |
| [6. Physics](06-physics.md) | Aether 2D + Bepu 3D, colliders, rigidbodies, character controllers, raycasts, units |
| [7. Input](07-input.md) | Action maps, keyboard/mouse/gamepad/touch, rebinding, cursor control |
| [8. Audio](08-audio.md) | `AudioManager`, buses, `AudioSource`, handles, fades, effects |
| [9. UI](09-ui.md) | `UiCanvas`, the node tree, layout, focus and navigation |
| [10. Animation](10-animation.md) | Sprite animation, animator state machines, tweens, skeletal animation |
| [11. JavaScript Scripting](11-scripting.md) | The real script API, lifecycle hooks, hot reload, extending the bridge |
| [12. Scenes & Prefabs](12-scenes-prefabs.md) | Scene JSON format, serialisation, prefabs, world streaming |
| [13. Assets](13-assets.md) | `AssetManager`, supported types, reference counting, bundles, hot reload |
| [14. Save & Preferences](14-save-system.md) | Save slots, encryption, binary saves, `PlayerPrefs` |
| [15. Networking](15-networking.md) | One wire for both engines: the transport seam, WebSocket and UDP, rooms and link-to-join, replication and RPC |
| [22. Gameplay Framework](22-gameplay-framework.md) | `GameInstance`, `GameMode`, `GameState`, `Pawn`, `Character`, controllers, subsystems |
| [23. AI](23-ai.md) | Blackboards, behaviour trees, navmesh, `AIController` |
| [24. Localization & Utilities](24-localization.md) | `Loc`, object pooling, 2D lighting, tilemap collision, 3D constraints, `SBMath`, `Bounds` |

## Tooling & shipping

| Page | What it covers |
|---|---|
| [16. The Editor](16-editor.md) | Project launcher, panels, the Create menu, gizmos, play mode, scene I/O |
| [17. Debugging & Profiling](17-debugging.md) | Debug overlay, gizmos, profiler, memory viewer, network diagnostics |
| [18. Build & Export](18-build-export.md) | `PlatformConfig`, asset cooking, the export pipeline, the CLI |
| [19. Steam](19-steam.md) | `SteamManager`, achievements, cloud, lobbies, workshop |
| [25. AI Assistant & MCP](25-ai-assistant-mcp.md) | Claude inside the editor: the Assistant panel, the MCP server and its tools, C# hot reload, engine rebuild + restart |
| [26. The HTML5 Port](26-html5.md) | The JavaScript engine under `html5/`, shared project files, the Web build target, and three bugs it found |
| [27. The Game Factory Workflow](27-game-factory-workflow.md) | Making many small games with an agent: the shared scripting contract, the node toolchain (validate, serve, export, upload), the native port, and the token rules |
| [28. The CookieJar](28-the-cookiejar.md) | The module library: cookies and jars, installing into a project, baking work back out, and the trust gate |
| [29. Darks Games](29-darksgames.md) | Accounts and social: identity, presence, Join, parties, cloud saves and achievements, in both engines and in the exported build |
| [30. MakeChibi](30-makechibi.md) | Characters built from primitives at runtime: the recipe, the sixteen-joint rig, procedural and keyed clips, the `Chibi` scripting global, and the editor panel |

## Reference

| Page | What it covers |
|---|---|
| [20. API Index](20-api-index.md) | Every public type, grouped by namespace, with its file |
| [21. Gotchas & Known Mismatches](21-gotchas.md) | The traps that will cost you an afternoon if you hit them cold |

---

## What the engine is

SexyBiscuit is a **MonoGame-based, actor/component game engine written in C#**,
with **JavaScript gameplay scripting** through [Jint](https://github.com/sebastienros/jint).
It targets .NET 8 and ships as three projects:

```
SexyBiscuit.sln
├── SexyBiscuit.Engine   net8.0          — the engine library
├── SexyBiscuit.Editor   net8.0          — ImGui visual editor (cross-platform)
├── SexyBiscuit.Demo     net8.0          — "Biscuit Chronicles" sample game
└── SexyBiscuit.Tests    net8.0          — unit tests over core, gameplay, AI, serialisation
```

Third-party foundations, from `SexyBiscuit.Engine.csproj`:

| Concern | Library |
|---|---|
| Windowing, graphics, audio device | MonoGame.Framework.DesktopGL 3.8.1 |
| JavaScript runtime | Jint 3.1.1 |
| 2D physics | Aether.Physics2D 2.1.0 |
| 3D physics | BepuPhysics / BepuUtilities 2.4.0 |
| 3D model import | AssimpNet 4.1.0 |
| Networking transport | LiteNetLib 1.1.0 |
| Steam | Steamworks.NET 20.1.0 |
| Runtime fonts | FontStashSharp.MonoGame 1.3.3 |
| Audio decoding | NAudio 2.2.1 |
| JSON | System.Text.Json 8.0.5 |

## Conventions used in this wiki

- **`Verified`** — the described behaviour was read directly from engine source.
- **`Not wired`** — the type exists and works, but nothing in the engine loop
  calls it; your game must drive it. See [page 3](03-game-loop.md).
- **`Design doc only`** — described in the root `README.md` but not present in
  the source at the time of writing.

Code samples target the public API as it exists in `SexyBiscuit.Engine/`. Where
a sample needs a workaround for a rough edge, the workaround is called out
rather than hidden.

## This engine is a moving target

**Verified against commit `3bde26e`.** The engine is under active development —
pages here were written by reading the source rather than the design document,
and the source moves.

Where a page makes a claim that would cost you time if it were stale, it carries
a **check** — a one-line command that re-derives the claim from the source.
[Page 21](21-gotchas.md) is built entirely that way.

If something in the wiki disagrees with the code, the code is right. The three
claims worth re-checking first, because everything else depends on them:

```bash
# 1. What does the frame actually pump?
sed -n '/protected override void Update(GameTime/,/^    }/p' SexyBiscuit.Engine/Engine.cs

# 2. Does anything open a SpriteBatch around the 2D scene draw?
sed -n '/protected override void Draw(GameTime/,/^    }/p' SexyBiscuit.Engine/Engine.cs

# 3. Which systems does the engine step for you?
grep -rn --include='*.cs' -e 'FixedStep(' -e 'Tween.UpdateAll' SexyBiscuit.Engine/Engine.cs
```

At the time of writing, the engine drives time, input, **physics**, the scene
graph, **tweens**, timers, coroutines, audio and the 3D render pass; your game
drives the 2D `SpriteBatch`, networking, script hot reload and the debug
tooling.
