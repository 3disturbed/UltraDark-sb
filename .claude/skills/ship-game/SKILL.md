---
name: ship-game
description: Ship a game that has proven fun - validate strictly, run both test suites, build every target with the sbengine CLI (web + desktop binaries), upload the archives, and report one line per target. Use for "ship X", "make the builds", "port to desktop", "release a dev build".
---

# Ship a game

Argument: the game folder, e.g. `Games/<name>`. Runs on the Mac with .NET and the engine
checkout (or in CI through `release.yml`).

## Steps

1. **Gate.** From `html5/`: `npm run validate -- ../Games/<name> --strict` must print OK, then
   `npm test`. From the root: `dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj`. Fix
   the game, not the gate.

2. **Settings.** `Games/<name>/BuildSettings.json` exists with `appName`, `version`,
   `startScene` and an `upload` section (see `wiki/18-build-export.md`). Bump `version`.

3. **Build everything.**

   ```bash
   dotnet build SexyBiscuit.Build/SexyBiscuit.Build.csproj -c Release
   dotnet run --no-build --project SexyBiscuit.Build -c Release -- --project Games/<name> --all --upload --quiet
   ```

   With `SB_UPLOAD_URL` and `SB_UPLOAD_TOKEN` set the archives are uploaded and the report
   carries the URLs. Without them, drop `--upload` and hand over the archives in `dist/`. In
   CI: `gh workflow run release.yml -f game=Games/<name> -f upload=true` then
   `gh run watch --exit-status`.

4. **Read the summary, not the log.** One line per target. Open the log (`dist/<Platform>/`
   is the staged folder; the CLI prints the error lines under a failed target) only for a
   target that failed.

5. **Optional C# layer.** Only for a documented reason (a feature the browser lacks, native
   performance): `create_code_project` / `create_class` through the editor session, keep the
   JavaScript as the source for the web build, rerun step 3.

6. **Report in four lines**: version and commit, the URLs (or where the archives are), the
   test totals, and this session's usage line (`node html5/tools/usage.js --latest`).

## Rules that keep the session cheap

- Builds run once, as one command; never rerun a target to "see the log again".
- Do not read the engine source to fix a build error; read the error line under the target.
- A failing publish is nearly always the engine checkout or the SDK: `SEXYBISCUIT_REPO`, and
  `dotnet --version`.
