# 25. AI Assistant & MCP

Project: `SexyBiscuit.Editor/Assistant` (host, panel, session) ·
`SexyBiscuit.Engine/Mcp` (protocol, scene tools, Claude Code plumbing) · **cross-platform**

```bash
dotnet run --project SexyBiscuit.Editor -- --assistant-selftest --dry-run
```

The editor is an MCP server, and it runs Claude Code. Claude builds and changes your game
inside the running editor while you watch: it places actors, sets materials, writes C# and
hot-reloads it, runs the game, and asks you when a decision matters. Everything it does shows
in the viewport as it happens, in the Assistant panel's Activity tab, and in the Output Log.
**Stop** cancels it; **Undo** reverts scene changes one tool call at a time.

---

## Two ways to connect

| | Embedded session | Terminal session |
|---|---|---|
| What runs | The editor spawns `claude` itself (`--print --input-format stream-json ...`) | You run `claude` in a terminal, inside the project folder |
| How it finds the editor | An inline `--mcp-config` carrying the live port | The project's `.mcp.json`, written when the project opens |
| Where you talk | The Assistant panel | Your terminal, plus `say` / `ask_user` / `wait_for_user` in the panel |
| Needs | A signed-in Claude Code binary | The same |

Both can be connected at once. The panel shows who is *driving*: the embedded session while
its process is alive, otherwise a terminal session that is blocked in `wait_for_user` or was
seen in the last two minutes. Text typed into the composer goes to the driver; with no driver
it starts an embedded session.

### Sign in once

The Claude Code binary needs its own sign-in; the Claude desktop app's does not carry over.
The first time, the panel shows **Claude Code is not signed in** with an **Open a terminal to
sign in** button: in that terminal type `/login`, finish in the browser, close it and press
**Resume**. An `ANTHROPIC_API_KEY` in the environment works too.

Where the binary comes from, in order: **Settings › Claude Code path**, `claude` on `PATH`,
the native installer (`~/.local/bin/claude`), `/usr/local/bin`, Homebrew, and the copy bundled
with the Claude desktop app (`~/Library/Application Support/Claude/claude-code/<version>/` on
macOS). Every candidate is run with `--version` and the first that answers wins, so a broken
npm shim on `PATH` is skipped rather than fatal. To install:
`curl -fsSL https://claude.ai/install.sh | bash` (Windows: `irm https://claude.ai/install.ps1 | iex`).

---

## The Assistant panel

Docked beside **Details**. **View › Assistant** toggles it, <kbd>F8</kbd> focuses the composer,
<kbd>Shift</kbd>+<kbd>F8</kbd> stops whatever Claude is doing.

**Header** — a state dot (grey idle, green connected, amber pulsing while working, red exited),
the model, the cost of this process (hover for the project's lifetime cost and token counts),
and the buttons: **Stop** interrupts the turn and cancels every running tool call;
**Start / Resume**; **New** starts a fresh session after confirming; **Settings**.

**Chat** — the transcript. Your messages are grey while queued or sending. Claude's text
renders light markdown: paragraphs, bullets, numbered lists, headings, and code blocks with a
copy button. Each tool call is one line (`[..]` running, `[ok]` done, `[!!]` failed) that expands
to its arguments and result; a `capture_viewport` result has a **Show image** button.
Questions from `ask_user` appear as cards with one button per choice and a free-text box;
permission prompts (in the stricter modes) as cards with Allow / Always allow / Deny; each turn
ends with a cost line. The view sticks to the bottom until you scroll up, and **Jump to
latest** brings it back. Quick actions above the composer send canned prompts. The composer
sends on <kbd>Enter</kbd>, <kbd>Ctrl</kbd>+<kbd>Enter</kbd> inserts a new line, and text typed
while a turn runs is queued and sent when the turn ends (the **x** withdraws it).

**Activity** — every MCP tool call the editor served, from any client: time, client, tool,
arguments, result and duration. Failed calls are red; click a row for details. Mutations and
failures are echoed to the Output Log as `[Claude] ...`.

**Diagnostics** — the binary and version in use and every candidate that was tried with its
verdict, the MCP server URL and its clients, the session id, the exact command line, the
`system/init` summary including `mcp_servers`, stderr, the last frames, the session file
(**Reveal**) and the settings file.

While Claude works the viewport carries a purple **CLAUDE: ...** banner and the toolbar shows
**Stop Claude**.

---

## Permission modes

Set in **Settings › Behaviour**; a change applies to the running session at once.

| Mode | `claude` flags | Behaviour |
|---|---|---|
| **Autonomous** (default) | `--permission-mode bypassPermissions --allow-dangerously-skip-permissions` | No prompts. The banner, the Activity log and Stop are the safety net. |
| Accept edits | `--permission-mode acceptEdits` | File edits inside the project go through; shell commands and the like prompt in the panel. |
| Ask | `--permission-mode manual` | Every non-read tool prompts in the panel. |
| Auto | `--permission-mode auto` | Claude Code's own classifier decides what needs a prompt. |

Editor tools are always pre-approved (`--allowedTools mcp__sexybiscuit`) and `AskUserQuestion`
is disabled (`--disallowedTools`), so questions go through `ask_user` and render in the panel.
A prompt nobody answers is denied after ten minutes.

---

## What Claude is told

The embedded session gets an appended system prompt (`ClaudeSystemPrompt.Embedded`): who it
is, the project's name and root, the engine repository when one is found (also passed as
`--add-dir`), and the working rules — start with `get_context` and ask for more only when a
task needs it, edit in batches with `apply_scene_edits` and trust the short results, prefer the
tools to editing `.scene` JSON, C# lives in `Source/` and is followed by `reload_game_code`,
`rebuild_engine_and_restart` is for engine changes only, verify at milestones (one
`capture_viewport` at 640 px, `read_console` with `sinceSequence`) rather than after every
edit, `ask_user` is for decisions that are expensive to reverse, and plain text with bullets
because the panel has one font. A terminal session receives the same guidance through MCP `initialize.instructions`,
plus "use `say` for anything the user should read and end every turn with `wait_for_user`".
The repository root's `CLAUDE.md` orients either kind of session.

---

## The tools

The `sexybiscuit` server exposes 86 tools (`mcp__sexybiscuit__<name>` inside Claude Code). This
table is generated: `dotnet run --project SexyBiscuit.Editor -- --dump-mcp-tools --all --markdown`
prints it without a window (`--all` adds the editor-only classes to the engine tools; the
running editor serves the same list as the `sexybiscuit://tools` resource). Results are sized
for an agent: one compact JSON text block led by a summary line, stubs (`id`, `name`, `layer`,
`position`) from every mutating tool, one line per actor from `get_scene_summary`, and
`get_context` for the state of things in about a hundred tokens. `apply_scene_edits` runs a
list of edits in one call and one undo step; `run_scene_report`, `run_tests` and
`export_build` return counts and one-line-per-target reports instead of logs.

| Tool | Description | Parameters |
|---|---|---|
| `add_component` | Add a component to an actor by type name (short names such as 'Light3D' work). Companion components the type requires are added automatically and reported. properties sets initial values by property name. | `actor`: string<br>`componentType`: string<br>`properties`: any JSON value (optional) (optional) |
| `add_cookie_jar` | Propose a new jar. This never clones and never trusts anything: it records the address and returns, and the person at the editor decides. Tell the user what you proposed and ask them to approve it in the Cookie Jar panel. | `urlOrPath`: string<br>`name`: string (optional) |
| `apply_scene_edits` | Run several scene edits in one call and one undo step. ops is a JSON array; each op is a tool's arguments plus "op": spawn_actor, spawn_primitive, place_actor, duplicate_actor, set_transform, translate, rotate, look_at, set_properties, set_property, add_component, remove_component, set_material, rename_actor, set_actor, move_to_layer, destroy_actor, attach_actor or detach_actor. An actor argument may be "$n": the id spawned by op n (0-based). | `ops`: any JSON value<br>`stopOnError`: boolean (default false) |
| `ask_user` | Ask the user a question in the editor and wait for the answer. Optional choices become buttons; the user can also type a free answer unless allow_free_text is false. Blocks until answered, or until timeout_seconds (default 900, 30-3600) passes. Returns {answer, choice_index, free_text}, or {status:'timeout'} / {status:'cancelled'}. Use it for decisions that are expensive to change later, not for routine choices. | `question`: string<br>`choices`: array of string (optional)<br>`allowFreeText`: boolean (default true)<br>`timeoutSeconds`: integer (default 900) |
| `bake_cookie` | Save reusable work from the open project back into the CookieJar as a new cookie. Give it the files, a one-line summary, and agent instructions saying how to wire it up in the next game. Do this whenever a task produces something a second game would want. | `id`: string<br>`name`: string<br>`summary`: string<br>`agentInstructions`: string<br>`files`: array of string<br>`tags`: array of string (optional)<br>`nextSteps`: array of string (optional)<br>`requires`: array of string (optional)<br>`version`: string (default "1.0.0")<br>`jar`: string (optional)<br>`overwrite`: boolean (default false)<br>`dryRun`: boolean (default false) |
| `build_project` | Compile the project's C# code with dotnet build and return structured diagnostics {file, line, column, code, severity, message}. Waits up to wait_seconds (default 40); if the build is still running you get status 'running' and a build_id to poll with get_build_status. Building alone does not change the editor — call reload_game_code (which builds for you) to make the new code live. The game compiles against the engine build this editor runs; after editing engine source call rebuild_engine_and_restart instead. | `waitSeconds`: integer (default 40) |
| `cancel_build` | Cancel a running build. | `buildId`: string |
| `capture_scene_from` | Render the scene from a camera pose of your choosing, as a PNG, without moving any camera. Give lookAt or rotation [pitch, yaw, roll] degrees. | `position`: array of number<br>`lookAt`: array of number (optional)<br>`rotation`: array of number (optional)<br>`fov`: number (default 60)<br>`width`: integer (default 640)<br>`height`: integer (default 360) |
| `capture_viewport` | A PNG screenshot of the editor viewport as it is rendered right now, downscaled to maxWidth. 640 px is enough to judge a scene and costs about a third of 1024; use 1024 only to read text. includeUi captures the whole editor window with its panels instead. | `maxWidth`: integer (default 640)<br>`includeUi`: boolean (default false) |
| `clear_console` | Clear the Output Log. | — |
| `create_class` | Generate a starter C# file for a component, actor, gamemode, playercontroller, character or tool (a static class with an [McpTool] method) under Source/<kind folder>/<Name>.cs in the project's namespace. Returns the path; edit it with your file tools, then call reload_game_code. Never overwrites an existing file. Creates the C# project first when the project has none. | `kind`: string<br>`name`: string<br>`namespace`: string (optional)<br>`folder`: string (optional) |
| `create_code_project` | Add a C# project to the open SexyBiscuit project: <Name>.csproj at the project root, Source/ with starter classes (a GameMode, PlayerController and Character, a Spinner component, an example [McpTool] class), a per-machine SexyBiscuit.props pointing at this engine, and .gitignore. Existing files are never overwritten unless overwrite is true. By default it then builds, hot-loads the assembly, and swaps a plain GameMode in the scene for the project's own. | `overwrite`: boolean (default false)<br>`build`: boolean (default true)<br>`swapGameMode`: boolean (default true) |
| `create_project` | Create a new project folder <directory>/<name> from a template (names from get_project_info; omit for an empty project) and open it. | `name`: string<br>`directory`: string<br>`template`: string (optional) |
| `describe_component_type` | The editable properties of a component type: name, type, enum values, default value, documentation, and whether the property is saved in the scene file. | `componentType`: string |
| `destroy_actor` | Remove an actor from the scene. Undo brings it back. | `actor`: string |
| `duplicate_actor` | Clone an actor with all of its components and properties, offset by delta world units (pixels for 2D). The copy is placed in the same layer and becomes selected. | `actor`: string<br>`newName`: string (optional)<br>`offset`: array of number (optional) |
| `export_build` | Export the open project from disk: stage assets, scenes and scripts per target, publish self-contained desktop players (publish=true; needs the engine source and the .NET SDK), archive them, and upload the archives (upload=true publishes the native archives to DarksGames; needs DG_BUILD_TOKEN). platforms take BuildSettings names or RIDs — web, win-x64, osx-arm64, linux-x64 — and default to those four. Save the scene first. Waits up to waitSeconds and returns one line per target; a longer run returns a job id for get_build_report. | `platforms`: array of string (optional)<br>`configuration`: string (optional)<br>`publish`: boolean (default true)<br>`upload`: boolean (default false)<br>`version`: string (optional)<br>`waitSeconds`: integer (default 120) |
| `find_actors` | Filter the open scene's actors by name substring, tag, component type, layer or class. Filters are optional and combine with AND. | `nameContains`: string (optional)<br>`tag`: string (optional)<br>`componentType`: string (optional)<br>`layer`: string (optional)<br>`class`: string (optional) |
| `focus_actor` | Select an actor and move the editor camera to frame it. | `actor`: string |
| `get_actor` | One actor. detail 'full' (default): transform in degrees, every component with its editable properties, bounds, selection; 'row': id, name, class, layer, tag, component types, position. actor is an id or an exact name. | `actor`: string<br>`detail`: string (default "full") |
| `get_build_report` | The report of an export_build run — the latest when jobId is omitted: state, and one line per target with the archive, its size, and the upload URL or the first errors. | `jobId`: string (optional) |
| `get_build_status` | Status and diagnostics of a build started by build_project, reload_game_code, create_code_project, run_standalone or rebuild_engine_and_restart (the latest when build_id is omitted). For an engine rebuild, state 'restarting' means the editor is about to restart — stop calling tools, wait 15-30 seconds, then call get_context. | `buildId`: string (optional) |
| `get_code_project` | Describe the open project's C# code project: csproj path, Source/ files, output DLL, the loaded assembly generation, whether it was compiled against the engine build this editor runs, and the last build. Read-only; use create_code_project to add one. | — |
| `get_context` | Where things stand, in about a hundred tokens: project, scene (name, path, actor count, layers, dirty flag, checks), selection, play state, C# project and last build. Call this first; get_scene_summary, get_actor and get_project_info give more when a task needs it. | — |
| `get_cookie` | Everything about one cookie: its manifest, what it provides, every file it would install, and its full AGENT.md instructions. | `id`: string |
| `get_editor_camera` | The editor camera's pose and which view mode the viewport is in. | — |
| `get_engine_repo` | Where the engine source is (repository root, engine/editor/test projects, solution), whether it is a git checkout and on which branch, the running editor's engine build id, and the exact dotnet commands to build the engine, the editor and the tests. Read this before editing engine code. | — |
| `get_material` | Read a MeshRenderer material. | `actor`: string<br>`materialIndex`: integer (default 0) |
| `get_play_state` | Playing and paused flags, fps, frame count, time scale and the scene name. | — |
| `get_project_info` | Describe the open project and the editor: root folder, asset/script/scene folders, the open scene and whether it has unsaved changes, play mode, the MCP URL, and the templates create_project accepts with descriptions. get_context is the cheap version; use this for the folders and the template list. | — |
| `get_property` | Read one property of a component (or of the actor itself with componentType 'Actor'). | `actor`: string<br>`componentType`: string<br>`property`: string |
| `get_scene_json` | Full dump of the open scene. format 'view' gives readable actor views with editable component properties (rotations in degrees, colours as hex); 'file' gives the exact .scene JSON that save_scene would write. | `format`: string (default "view")<br>`layer`: string (optional)<br>`offset`: integer (default 0)<br>`maxActors`: integer (default 50) |
| `get_scene_summary` | The scene at a glance: a header (layers, dirty flag, checks for camera, light, player start, game mode) then one line per actor — id, name, class, layer, tag, components, position. Page with offset and limit; compact=false gives the same as JSON. Ids change after undo, redo or load. | `compact`: boolean (default true)<br>`layer`: string (optional)<br>`offset`: integer (default 0)<br>`limit`: integer (default 100)<br>`includeComponents`: boolean (default true) |
| `get_selection` | The actor and layer currently selected in the editor, if any. | — |
| `get_session_usage` | This session's token meter, about seventy tokens: turns, context per API call, cache share, output tokens, the size of the tool results, cost, the last turn, and the tools that returned the most. Read it to see what a task cost before repeating the pattern. | `topTools`: integer (default 5) |
| `install_cookie` | Copy a cookie into the open project, with anything it requires, then build and hot-reload if it added C#. The result carries the cookie's AGENT.md and its next steps: follow those rather than reading its files. Nothing is written when the plan is blocked; read the conflicts and fix them. | `id`: string<br>`includeDependencies`: boolean (default true)<br>`dryRun`: boolean (default false)<br>`overwrite`: boolean (default false) |
| `list_actor_classes` | Actor classes you can place or name in spawn_actor's class: the engine's gameplay classes (Actor, GameMode, Character, PlayerController…) and the project's own, with source 'engine' or 'project', base class and doc summary. | `source`: string (optional) |
| `list_actor_presets` | The palette presets with category, description and the components each one creates. | — |
| `list_assets` | Files under the project's asset directories with a type: texture, audio, model, script, scene, font or other. | `subdirectory`: string (optional)<br>`extensions`: array of string (optional)<br>`limit`: integer (default 100) |
| `list_component_types` | The component types that can be added, grouped by category (Rendering, Physics, Gameplay, Audio, Animation, AI, UI, Scripting…). Names only by default; namesOnly=false adds a one-line description, required companions and whether each comes from the engine or the project. | `category`: string (optional)<br>`search`: string (optional)<br>`namesOnly`: boolean (default true) |
| `list_cookie_jars` | The jars the catalogue is built from, and whether each may be installed from. | — |
| `list_installed_cookies` | What this project has installed, from CookieJar.lock.json, plus any file that has been edited or deleted since it was installed. | — |
| `list_scenes` | The .scene files in the project, as project-relative paths, marking the open one. | — |
| `load_scene` | Load a .scene file — path relative to the project root, extension optional — and make it the open scene. Refused during play mode. Unsaved changes are lost (undo can bring them back). | `path`: string |
| `log_message` | Write a line to the Output Log, prefixed [Claude]. | `message`: string<br>`level`: string (default "info") |
| `look_at` | Aim a 3D actor's forward axis (-Z, which is where cameras and lights point) at a world point or at another actor. | `actor`: string<br>`target`: array of number (optional)<br>`targetActor`: string (optional) |
| `move_to_layer` | Move an actor to another scene layer (a draw-order group), creating the layer if needed. Nothing is destroyed. | `actor`: string<br>`layer`: string<br>`order`: integer (default 0) |
| `attach_actor` | Attach an actor to a parent, so it moves, rotates and scales with it and is destroyed with it. Both the 2D and 3D transforms follow. Pass no parent to detach. | `actor`: string<br>`parent`: string (optional)<br>`keepWorldTransform`: boolean (default true) |
| `detach_actor` | Detach an actor from its parent, returning it to the scene root. It keeps its world position and stops being destroyed with the parent. | `actor`: string<br>`children`: boolean (default false) |
| `new_scene` | Replace the open scene with a fresh one. template 'default3d' gives a sky, two lights, a floor, a few shapes, a player start and a game mode; 'default2d' a 2D camera; 'empty' just the default layers. Unsaved changes are lost (undo can bring the previous scene back). | `name`: string (default "Untitled")<br>`template`: string (default "default3d") |
| `open_project` | Open a project from its .sbproject file, or from a folder that contains one. Its default scene is loaded when the file exists. | `path`: string |
| `pause` | Pause or resume play mode. Omit paused to toggle. | `paused`: boolean (optional) (optional) |
| `place_actor` | Place a palette preset — the same list as the editor's Place Actors panel: Empty Actor, Empty Actor (3D), Mesh, Skinned Mesh, Skybox, Directional Light, Point Light, Spot Light, 2D Light, Camera, Fly Camera, Camera 2D, Game Mode, Character, AI Character, Player Start, Particle System (3D), Particle Emitter (2D), Sprite, Tilemap, Canvas, World Canvas. Call list_actor_presets for descriptions. | `preset`: string<br>`name`: string (optional)<br>`position`: array of number (optional)<br>`rotation`: array of number (optional)<br>`layer`: string (optional)<br>`properties`: any JSON value (optional) (optional) |
| `play` | Start play mode (F5). The scene is snapshotted; changes made while playing are discarded on stop. | — |
| `publish_build` | Publish archives that already exist to DarksGames — no rebuild, so a twenty-minute desktop publish is not repeated just to send the file. Takes the archives from the last export_build in this session, or from the project's dist/build-report.json, or one explicit archive path. Only native builds publish: the web build is not a downloadable game build and is skipped. Re-publishing the same slug, version and platform replaces that build in place and keeps its download link working, so bump version for a genuinely new build. Needs DG_BUILD_TOKEN in the environment or on the first line of ~/.sexybiscuit/dg-token. | `platforms`: array of string (optional)<br>`channel`: string (optional)<br>`notes`: string (optional)<br>`requirements`: string (optional)<br>`appSlug`: string (optional)<br>`version`: string (optional)<br>`hidden`: boolean (default false)<br>`replace`: boolean (default true)<br>`archive`: string (optional)<br>`waitSeconds`: integer (default 900) |
| `read_console` | Read the editor's Output Log. Pass the latestSequence from the previous result as sinceSequence to get only new entries. level filters to that severity and above: info, warning, error. | `sinceSequence`: integer (default 0)<br>`level`: string (optional)<br>`contains`: string (optional)<br>`limit`: integer (default 50) |
| `rebuild_engine_and_restart` | Rebuild the engine and the editor from source and restart the editor so engine changes take effect. The build runs into a staging folder first, so a failure leaves the running editor untouched and returns diagnostics without restarting. On success the editor saves the scene, writes a resume file, and restarts a couple of seconds after this result is delivered; it reopens the same project and scene, restores the selection, and resumes the assistant session. While it restarts, MCP calls fail for 10-30 seconds: stop calling tools, wait, then call get_context until it answers, and re-list tools. Never repeat the rebuild. | `configuration`: string (optional)<br>`runTests`: boolean (default false)<br>`waitSeconds`: integer (default 40) |
| `redo` | Redo N undone changes. Actor ids are regenerated. | `steps`: integer (default 1) |
| `refresh_cookie_jar` | Fetch a trusted git jar and report what changed, including any cookie this project has installed. | `name`: string |
| `reload_game_code` | Build the C# project (unless build=false) and hot-reload the assembly into the running editor: the scene is serialised, the old assembly unloaded, the new one loaded and the scene restored with unsaved edits intact. Play mode is stopped first. Reports which actor and component classes and which game_ tools appeared or disappeared. Refuses when the game was compiled against a different engine build than this editor runs — call rebuild_engine_and_restart — unless allow_engine_mismatch is true. | `build`: boolean (default true)<br>`stopPlayMode`: boolean (default true)<br>`allowEngineMismatch`: boolean (default false)<br>`waitSeconds`: integer (default 40) |
| `remove_component` | Remove the first component of a type from an actor. The 2D Transform cannot be removed; remove a Transform3D only if nothing else on the actor needs it. | `actor`: string<br>`componentType`: string |
| `rename_actor` | Rename an actor. | `actor`: string<br>`newName`: string |
| `rotate` | Rotate an actor by delta degrees [pitch, yaw, roll] (or [degrees] for 2D), composed with its current rotation. | `actor`: string<br>`delta`: array of number<br>`space`: string (default "world") |
| `run_scene_report` | Play the open scene for a few seconds and report what happened in about a hundred tokens: frames and fps, script errors, console warnings and errors (deduplicated, newest last) and the actor count at the end. Play mode is exited and the scene restored afterwards. Use it in place of play, wait, read_console and stop. | `seconds`: number (default 3)<br>`scene`: string (optional)<br>`maxLines`: integer (default 10) |
| `run_standalone` | Build the project (a full build, engine included) and launch the game as its own process with the project root as working directory, using ProjectSettings.json and its StartScene. Its output streams into the Output Log tagged [Game]. A previous instance is stopped first. | `waitSeconds`: integer (default 120) |
| `run_tests` | Run a test suite and return the totals and the failing names, not the log. project: 'engine' (the engine's xunit suite), 'templates' (only the template smoke tests, which run every template's scripts on the C# engine), 'html5' (npm test in html5/: the JavaScript engine and the tools) or 'lint' (npm run lint). filter narrows engine tests by name (FullyQualifiedName~filter) or html5 tests by pattern. Blocks until the run finishes or waitSeconds pass; the first run after a change includes a build. | `project`: string (default "engine")<br>`filter`: string (optional)<br>`waitSeconds`: integer (default 600) |
| `save_scene` | Save the open scene to disk. Omit path to save where it was loaded from or last saved; otherwise give a project-relative path such as 'Scenes/Level1.scene'. Refused during play mode; refuses paths outside the project. | `path`: string (optional) |
| `say` | Show a message to the user in the editor's Assistant panel. For sessions driving the editor from a terminal, this is how the user reads you; the embedded assistant's own replies already appear there and need not call it. | `message`: string<br>`level`: string (optional) |
| `search_cookies` | Search the CookieJar: the team's library of ready-made modules (a character controller, an input map, a HUD). Call this before writing a common mechanic by hand. Returns one line per cookie with what it provides; install_cookie then copies one into the open project and tells you how to wire it up. | `query`: string (optional)<br>`tags`: array of string (optional)<br>`engine`: string (optional)<br>`includeInstalled`: boolean (default true)<br>`limit`: integer (default 20) |
| `select_actor` | Select an actor in the editor so it shows in the Details panel and wears the gizmo. Omit actor to clear the selection. | `actor`: string (optional) |
| `set_actor` | Set an actor's tag, active flag, physics layer (the integer Actor.Layer used by collision masks) or lifeSpan in seconds (0 = forever). | `actor`: string<br>`tag`: string (optional)<br>`active`: boolean (optional) (optional)<br>`physicsLayer`: integer (optional) (optional)<br>`lifeSpan`: number (optional) (optional) |
| `set_auto_reload` | Turn automatic build + hot-reload on file save in Source/ on or off. Off by default; you normally call reload_game_code explicitly. | `enabled`: boolean |
| `set_editor_camera` | Move the editor camera (not any scene camera). Give lookAt or rotation [pitch, yaw, roll] degrees. | `position`: array of number (optional)<br>`lookAt`: array of number (optional)<br>`rotation`: array of number (optional) |
| `set_material` | Set a MeshRenderer material: albedo colour, metallic 0-1, roughness 0-1, emissive intensity, and texture paths relative to the project root. Only the given fields change. The material is saved with the scene. | `actor`: string<br>`albedoColor`: colour '#RRGGBB[AA]' or name (optional) (optional)<br>`metallic`: number (optional) (optional)<br>`roughness`: number (optional) (optional)<br>`emissiveIntensity`: number (optional) (optional)<br>`albedoTexture`: string (optional)<br>`normalTexture`: string (optional)<br>`materialIndex`: integer (default 0) |
| `set_properties` | Set several properties on one actor at once. Keys are 'ComponentType.Property' (or 'Actor.Property'), e.g. {"Light3D.Intensity": 2, "Transform3D.Position": [0, 3, 0]}. Valid entries are applied even if others fail. | `actor`: string<br>`properties`: any JSON value |
| `set_property` | Set one property on a component (or on the actor itself with componentType 'Actor'). Value formats: numbers, booleans, strings, enum names, vectors as [x, y, z], rotations as [pitch, yaw, roll] degrees, colours as '#RRGGBB', '#RRGGBBAA', a colour name or {r, g, b, a}. Use 'Transform3D' for Position/EulerAngles/Scale. | `actor`: string<br>`componentType`: string<br>`property`: string<br>`value`: any JSON value |
| `set_transform` | Set position, rotation and/or scale. 3 elements address the Transform3D (added if missing) — rotation is [pitch, yaw, roll] in degrees; 2 elements address the 2D transform — rotation is [degrees]. space 'local' sets values relative to the parent. | `actor`: string<br>`position`: array of number (optional)<br>`rotation`: array of number (optional)<br>`scale`: array of number (optional)<br>`space`: string (default "world") |
| `set_viewport` | Switch the viewport between 3D and 2D rendering, between the editor camera and the scene's MainCamera3D, or (while playing) between the docked viewport and the game over the whole window. Play always starts on the game camera. | `view3d`: boolean (optional) (optional)<br>`useGameCamera`: boolean (optional) (optional)<br>`fullscreen`: boolean (optional) (optional) |
| `spawn_actor` | Create an actor. components are type names such as 'MeshRenderer' or 'Light3D' (required companions are added automatically); class is an Actor subclass such as 'GameMode', 'Character' or a project class. position/rotation/scale with 3 elements make a 3D actor (rotation is [pitch, yaw, roll] degrees); 2 elements make a 2D one (rotation [degrees]). properties sets initial values, keyed 'Type.Property'. The new actor becomes selected. | `name`: string<br>`components`: array of string (optional)<br>`class`: string (optional)<br>`position`: array of number (optional)<br>`rotation`: array of number (optional)<br>`scale`: array of number (optional)<br>`layer`: string (default "default")<br>`tag`: string (optional)<br>`properties`: any JSON value (optional) (optional)<br>`transform3d`: boolean (default true) |
| `spawn_many` | Spawn one primitive shape (Cube, Sphere, Plane, Quad, Cylinder, Cone) or one palette preset at several positions, or on a grid. Returns ids, names and positions only. | `what`: string<br>`positions`: array of array of number (optional)<br>`grid`: array of integer (optional)<br>`spacing`: number (default 2)<br>`origin`: array of number (optional)<br>`name`: string (optional)<br>`scale`: array of number (optional)<br>`color`: string (optional)<br>`layer`: string (optional)<br>`tag`: string (optional) |
| `spawn_primitive` | Place a built-in shape with its own coloured material: Cube, Sphere, Plane (1x1 floor tile), Quad (1x1 wall), Cylinder or Cone, all unit-sized — use scale for dimensions. The new actor becomes selected. | `shape`: string<br>`name`: string (optional)<br>`position`: array of number (optional)<br>`rotation`: array of number (optional)<br>`scale`: array of number (optional)<br>`color`: colour '#RRGGBB[AA]' or name (optional) (optional)<br>`metallic`: number (default 0)<br>`roughness`: number (default 0.5)<br>`layer`: string (optional)<br>`tag`: string (optional) |
| `step_frame` | While paused, advance the simulation by N frames of 1/60 s. | `frames`: integer (default 1) |
| `stop` | Stop play mode (F7) and restore the scene as it was when play started. | — |
| `stop_standalone` | Stop the game process started by run_standalone. | — |
| `translate` | Move an actor by a delta. space 'local' moves along the actor's own axes. | `actor`: string<br>`delta`: array of number<br>`space`: string (default "world") |
| `undo` | Undo the last N scene changes made through these tools (also Edit > Undo in the editor). The scene is restored from a snapshot, so actor ids are regenerated — re-query them. | `steps`: integer (default 1) |
| `uninstall_cookie` | Remove a cookie from the open project. A file is deleted only when it still matches what was installed, so anything edited since is kept and reported. | `id`: string<br>`force`: boolean (default false) |
| `wait_for_user` | For sessions driving the editor from a terminal: block until the user types something in the editor's Assistant panel, then return it as {status:'prompt', text}. Call it at the end of every turn and act on the result. Also returns {status:'timeout'} after timeout_seconds (default 900, 10-3600), {status:'cancelled'} when the user presses Stop, {status:'editor_closing'}, or {status:'not_needed'} for the embedded assistant, whose user messages arrive directly. | `timeoutSeconds`: integer (default 0) |

A project's own `[McpTool]` methods join the list with a `game_` prefix after
`reload_game_code`, and leave it when the class disappears; Claude Code is told through
`notifications/tools/list_changed`.

### Resources and prompts

| Resource | What it is |
|---|---|
| `sexybiscuit://guide` | The workflow guide, markdown |
| `sexybiscuit://scene/current` | The open scene as readable actor views |
| `sexybiscuit://scene/current.scene` | The exact JSON `save_scene` writes |
| `sexybiscuit://components` | Every component type with its editable properties |
| `sexybiscuit://actor-presets` | The Place Actors palette |
| `sexybiscuit://scripting/api.d.ts` | TypeScript definitions for the JavaScript bridge |
| `sexybiscuit://tools` | This catalogue |
| `sexybiscuit://cookies` | The CookieJar catalogue: every reusable module and what it provides |
| `sexybiscuit://cookies/installed` | What this project has installed |

The prompt `build_level(brief)` asks for a level from a one-line description, and
`use_a_cookie(mechanic)` asks for one to be found in the library and wired up.

### Cookies

Nine of the tools are the module library: `search_cookies`, `get_cookie`,
`install_cookie`, `uninstall_cookie`, `list_installed_cookies`, `bake_cookie`,
`list_cookie_jars`, `add_cookie_jar` and `refresh_cookie_jar`. The assistant is
told to search the jar before writing a common mechanic by hand, and to bake
reusable work back when a task produces something a second game would want.

Installing compiles and runs the cookie's code, so a jar that is not the engine's
own has to be trusted by a person: `add_cookie_jar` records an address and never
clones, and installing from a non-builtin jar raises a question in the editor.
See [28. The CookieJar](28-the-cookiejar.md).

---

## C# projects and hot reload

`create_code_project` adds `<Name>.csproj` at the project root, `Source/` with a `GameMode`,
a `PlayerController`, a `Character`, a `Spinner` component and an example `[McpTool]` class, a
per-machine `SexyBiscuit.props` that names the engine build, and a `.gitignore`.
`build_project` compiles against the engine build the editor is running
(`-p:BuildProjectReferences=false`) and returns structured diagnostics. `reload_game_code`
builds, serialises the live scene, swaps the assembly (a collectible `AssemblyLoadContext`)
and restores the scene with unsaved edits intact; new actor and component classes then appear
in Place Actors, Add Component, `list_actor_classes` and the serialiser, and `[McpTool]`
methods become `game_*` tools. The **C# Project** panel shows the same build and reload state
with clickable diagnostics.

`rebuild_engine_and_restart` is for engine source. It builds the editor into a staging folder
(a failed build leaves the running editor untouched and returns the errors), saves the scene,
writes a resume file and relaunches the editor, which reopens the project, scene and
selection and resumes the Claude session with a message beginning `[editor] The SexyBiscuit
editor restarted`. MCP calls fail for 10–30 seconds while that happens; the tool's result tells
Claude to wait and retry `get_project_info`.

---

## Sessions and cost

One Claude Code session per project, keyed by project root in `assistant-settings.json`.
Opening a project resumes it (`--resume <id>`); **New** forgets it and starts fresh; a session
whose transcript Claude Code no longer has is replaced automatically. Cost is cumulative per
process in the header and per project across sessions in the tooltip (`LifetimeCostUsd`).
**Settings › Limits › Max spend per session** passes `--max-budget-usd`.

Claude Code keeps the transcript itself under `~/.claude/projects/<project path>/<id>.jsonl`;
Diagnostics has a **Reveal** button for it.

The session meter (`SessionUsage`) records every completed turn: the result frame's input,
output, cache-read and cache-creation tokens and API calls, the cost delta, and the size of
every tool result the transcript saw, grouped by tool. Each turn is appended as one line to
`<project>/.sexybiscuit/usage.jsonl`; the cost tooltip shows context per API call, the cache
share and the tool-result volume; `get_session_usage` returns the same in about seventy tokens
so a session can see what a task cost before repeating the pattern; the per-project record
keeps lifetime context, output and cache-read totals. `npm run usage -- <transcript>` in
`html5/` reads a terminal session's transcript the same way
([27. Game factory workflow](27-game-factory-workflow.md#measuring)).

---

## Settings

`%APPDATA%/SexyBiscuit/assistant-settings.json` (`~/Library/Application Support/SexyBiscuit/`
on macOS), edited from **Settings** in the panel or **Tools › Assistant Settings...**:

| Key | Default | Meaning |
|---|---|---|
| `McpPort` | `7331` | first port to try; the server walks upward when it is taken |
| `McpToken` | null | bearer token every client must send; null means loopback-only, no auth |
| `WriteProjectMcpConfig` | `true` | write the server entry into the project's `.mcp.json` |
| `ClaudePath` | null | a specific binary; null searches |
| `Model`, `Effort` | `""` | `--model` / `--effort` when set |
| `PermissionMode` | `Autonomous` | see above |
| `Thinking` | `Omitted` | `Summarized` shows thinking summaries, collapsed |
| `MaxBudgetUsdPerSession` | null | `--max-budget-usd` |
| `AutoStartOnProjectOpen` | `true` | start or resume when a project opens |
| `IncludeEngineRepo`, `EngineRepoPath` | `true`, null | give the session the engine checkout (found from the editor binary, or set here) |
| `IncludeProjectMcpServers` | `false` | false adds `--strict-mcp-config` |
| `TranscriptMaxEntries` | `2000` | rows kept in the panel |
| `WaitForUserDefaultTimeoutSeconds` | `900` | how long `wait_for_user` blocks without a timeout argument |
| `ExtraArgs` | `[]` | appended to the command line verbatim |
| `Sessions` | | per-project records: id, version, model, lifetime cost, turns |

---

## Headless checks

```bash
dotnet run --project SexyBiscuit.Editor -- --assistant-selftest [--dry-run] [--prompt "..."] [--timeout 180] [--mcp-port N]
dotnet run --project SexyBiscuit.Editor -- --dump-mcp-tools [--all] [--markdown] [--budget N]
```

`--dump-mcp-tools` prints the catalogue as `tools/list` JSON (or a markdown table) without a
window: the engine and interaction tools, plus the editor-only classes with `--all`, which
registers them without their constructors because only their attributes are read. `--budget N`
fails the run when the compact JSON is longer than N characters; CI holds the full catalogue
under 60,000 characters (49,800 today, about 12,500 tokens, the price a session pays once when
Claude Code loads the tool set).

The self-test lists every binary candidate with its verdict, starts a headless MCP server with
the engine and interaction tools, prints the exact command line and, unless `--dry-run`, runs
one canned conversation: Claude must call `get_project_info`, `say("SELFTEST-OK")` and answer
`DONE`. Each frame is printed, then a PASS/FAIL checklist; a binary that is not signed in is
diagnosed with the command to run.

---

## Protocol notes

JSON-RPC 2.0 over Streamable HTTP at `http://127.0.0.1:7331/mcp/`, on `System.Net.HttpListener`
with no ASP.NET, stateless: no session ids, so a client that reconnects after an idle timeout
simply carries on. Fast calls answer as JSON; anything over a second switches to server-sent
events with `: keepalive` comments so Claude Code's HTTP client never idles out, and `GET`
opens a stream that carries `tools/list_changed` after a hot reload. Protocol versions
2025-06-18, 2025-03-26 and 2024-11-05 are accepted. Loopback only by default; a bearer token
can be required. Tools marked `MainThread` run at the top of `EditorApp.Update`, so every
change is drawn the same frame; `ask_user`, `wait_for_user` and builds run off the main thread
and block on a task. Every mutating tool snapshots the scene for Undo first. The `.mcp.json`
entry asks for a one-hour per-call timeout because a question to the user can take that long.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "Claude Code was not found" | Install it, or point **Settings › Claude Code path** at a binary. Diagnostics lists every location tried and why each failed; `claude native binary not installed` is the Homebrew npm shim. |
| "Claude Code is not signed in" | Press **Open a terminal to sign in**, type `/login`, then **Resume**. Or set `ANTHROPIC_API_KEY`. |
| `mcp_servers: sexybiscuit=failed` in Diagnostics | Port mismatch, or a token the session did not send. Check the MCP server line and restart the session. |
| Port 7331 is taken | The server walks up to 7341 and the panel and `.mcp.json` follow. `--mcp-port N` or `McpPort` pins one. |
| Claude Code refuses to start inside another session | The editor was launched from a Claude Code terminal. The child environment is scrubbed of `CLAUDECODE` and `CLAUDE_CODE_*`; if it still happens, the Diagnostics stderr says which marker survived. |
| Windows: an npm `claude.cmd` does nothing | `.cmd` shims run through `cmd.exe`; prefer the native installer's `claude.exe`. |
| A terminal session never sees my message | It only receives text when it calls `wait_for_user`; the panel says so after a minute. |
| Tools missing after `reload_game_code` | Ask Claude to re-list tools or start a new turn; the change notification arrives on the next request. |

**Check** — `dotnet run --project SexyBiscuit.Editor -- --assistant-selftest --dry-run` prints the
candidates and the command line the editor would use.

---

## Next

- [16. The Editor](16-editor.md)
- [Tutorial 20: Building a Game with Claude](../tutorials/20-building-a-game-with-claude.md)
