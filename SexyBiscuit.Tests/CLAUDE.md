# SexyBiscuit.Tests — the engine's xunit suite

One flat folder, one class per concern; the class name says the area (`Ui*`, `Networking*`,
`Mcp*`, `Chibi*`, `Template*`, `CookieJar*`, `*Parity*`). About 860 facts in about 40 s after
the build. The project references the engine only; editor code has no tests here.

## Run one area

    dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~Ui"
    dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~Parity"     # the C#/JS pins
    SEXYBISCUIT_SLOW_TESTS=1 dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~BundledCookieTests"

## Rules that apply here only

- A name reads as a sentence, `ARoundTripPreservesActorIdentity`, and the class carries a
  why-comment saying which bug or contract it pins.
- `scene.FlushPendingActors()` after every mutation before reading back; `scene.Destroy()` in a
  `finally`, or the static registries leak into the next test.
- The parity classes read `../html5` by path through `EngineRepoLocator.Find()`; never copy JS or
  JSON into a test. Shared cases live in `html5/tests/fixtures/` and are read by both suites.
- Harnesses to reuse before writing one: `SceneToolHarness`, `CookieToolHarness`, `McpTestServer`,
  `ScriptProject`, `HeadlessSceneHost`. Only `CoreTests` needs a graphics device.
- Two slow tests hide behind `SEXYBISCUIT_SLOW_TESTS=1`; CI runs the cookie one on every push.
