# Contributing

## Building

```bash
dotnet build SexyBiscuit.Engine/SexyBiscuit.Engine.csproj
dotnet test  SexyBiscuit.Tests/SexyBiscuit.Tests.csproj
```

The editor is `net8.0-windows` and only runs on Windows. Everything else is
cross-platform, and CI builds all three desktop OSes.

## Before you open a PR

- **The engine builds with zero warnings.** CI enforces this with `-warnaserror`. If your
  change adds a warning, fix it rather than suppressing it — unless the suppression itself
  is the right call, in which case say why in a comment.
- **Add tests for logic that can be tested without a GPU.** Maths, pathfinding, state
  machines, serialization and lifecycle all can. Rendering largely cannot; that is fine.
- **Keep XML doc comments on public API.** `GenerateDocumentationFile` is on, so the docs
  ship in IntelliSense. CS1591 is suppressed for self-describing properties, but anything
  with behaviour worth knowing about should say so.

## Style

Match the surrounding file. In general:

- Four-space indentation, braces on their own line.
- `// ---` banner comments separating sections in longer files.
- British spelling in prose and identifiers where the codebase already uses it
  (`Colour` in `ColourGradePass`, `AlbedoColor` from the graphics API convention). When in
  doubt, match the neighbours.
- Comments explain *why*, not *what*. The code already says what it does.

## Where things go

| Namespace | For |
|---|---|
| `Core` | Actors, components, scenes, time, and the primitives everything else uses |
| `Gameplay` | The game mode / pawn / controller framework |
| `Rendering` | Anything that draws |
| `Physics` | Aether and Bepu integration |
| `AI` | Blackboards, behaviour trees, navigation |
| `Animation`, `Audio`, `Input`, `UI` | Their respective systems |
| `Networking`, `Steam` | Multiplayer and platform integration |
| `Scripting` | The Jint runtime and its bridge |

A new subsystem gets its own folder and namespace, plus a page in `wiki/`.

## Documentation

Changing behaviour means changing the docs. [`wiki/`](wiki/README.md) is the reference,
[`tutorials/`](tutorials/README.md) is the guided path, and the root
[`README.md`](README.md) is the tour. Keep all three honest — a documented feature that
does not exist is worse than an undocumented one that does.
