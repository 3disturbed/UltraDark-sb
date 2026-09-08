---
paths:
  - "SexyBiscuit.Engine/UI/**"
  - "SexyBiscuit.Engine/Rendering/**"
  - "SexyBiscuit.Engine/Chibi/**"
  - "html5/src/ui/**"
  - "html5/src/rendering/**"
  - "html5/src/chibi/**"
---
# Mirrored systems and their shared tables

`UI/`, `Rendering/` and `Chibi/` are mirrored file for file in `html5/src/ui`, `rendering` and
`chibi` (property for property in the UI). The data they share is JSON under `html5/src`,
embedded into the C# assembly by the csproj: font5x7, graphics-presets, graphics-menu,
sky-gradient, chibi-parts, chibi-clips. The layout, build and navigation fixtures under
`html5/src/ui/layout-cases.json` and `html5/tests/fixtures/` are read by both suites.

- Change the table, not two copies. A property added to a mirrored class is added to its twin in
  the same commit.
- Gate: from `html5/`, `node --test tests/ui*.test.js tests/graphics*.test.js tests/chibi.test.js`;
  then `dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~Ui"`
  (or `~Graphics`, `~Chibi`).
- `layerDepth` sorts ascending on both engines and higher is nearer; `spriteSortMode.test.js`
  reads `RenderSystem2D.cs` to pin it.
