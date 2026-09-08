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
`capture_viewport` at 640 px, `console` with `sinceSequence`) rather than after every
edit, `ask_user` is for decisions that are expensive to reverse, and plain text with bullets
because the panel has one font. A terminal session receives the same guidance through MCP `initialize.instructions`,
plus "use `say` for anything the user should read and end every turn with `wait_for_user`".
The repository root's `CLAUDE.md` orients either kind of session.

---

## The tools

The `sexybiscuit` server exposes 52 tools (`mcp__sexybiscuit__<name>` inside Claude Code). This
table is generated: `dotnet run --project SexyBiscuit.Editor -- --dump-mcp-tools --all --markdown`
prints it without a window (`--all` adds the editor-only classes to the engine tools; the
running editor serves the same list as the `sexybiscuit://tools` resource). Results are sized
for an agent: one compact JSON text block led by a summary line, stubs (`id`, `name`, `layer`,
`position`) from every mutating tool, one line per actor from `get_scene_summary`, and
`get_context` for the state of things in about a hundred tokens. `apply_scene_edits` runs a
list of edits in one call and one undo step; `run_scene_report`, `run_tests` and
`export_build` return counts and one-line-per-target reports instead of logs.

Each entry also carries the hints a client reads before calling: `readOnlyHint` for a tool that
changes nothing anywhere (12 of the 52), and `destructiveHint` for one worth confirming.
`readOnlyHint` used to be inferred from whether a tool edited the *scene*, which meant everything
that wrote a file, spawned a process or published a build claimed to be read-only —
`publish_build` among them, and `uninstall_cookie` claiming both at once. It is opt-in now, and
a merged tool with a writing mode does not carry it: `console` reads, writes and clears, and
`open_project` creates.

### One tool per job

There were 88 tools until 2026-09. The catalogue is loaded once per session by every agent
before it does anything, so its size is a fixed tax on every task, and the tools were fine-grained
in a way that spent it twice: `translate`, `rotate` and `look_at` were `set_transform` with one
argument each; `play`, `pause`, `stop`, `step_frame` and `get_play_state` were five tools over
one flag. Thirty-six of them merged into the survivor that already carried their arguments,
which cut the catalogue from 47,494 characters to 40,433, about 10,100 tokens.

An absorbed name is gone, not aliased. The merges, survivor first:

| Survivor | Absorbed | How |
|---|---|---|
| `spawn_actor` | `spawn_primitive`, `place_actor`, `list_actor_presets` | `shape` for a built-in mesh, `preset` for a palette entry, `list=true` for the palette |
| `set_actor` | `rename_actor`, `move_to_layer` | `name` renames, `layer` moves |
| `set_transform` | `translate`, `rotate`, `look_at` | `relative=true` for deltas, `lookAt`/`lookAtActor` to aim |
| `attach_actor` | `detach_actor` | no `parent` detaches, `children=true` detaches the children |
| `set_properties` | `set_property` | one `'Type.Property'` key is one property |
| `get_actor` | `get_property` | `property` reads one `'Type.Property'` |
| `describe_components` | `list_component_types`, `describe_component_type` | `type` names one to describe in full |
| `get_scene_summary` | `get_scene_json` | `format` is `compact`, `json`, `view` or `file` |
| `play_mode` | `play`, `pause`, `stop`, `step_frame`, `get_play_state` | `action`, defaulting to `status` |
| `editor_camera` | `get_editor_camera`, `set_editor_camera`, `set_viewport`, `focus_actor` | no arguments reads; `focus` frames an actor |
| `capture_viewport` | `capture_scene_from` | `position` renders from a pose instead |
| `select_actor` | `get_selection` | no `actor` reads the selection, `clear=true` deselects |
| `console` | `read_console`, `clear_console`, `log_message` | `message` writes, `clear` empties |
| `list_assets` | `list_scenes` | `type='scene'`; the Scenes folders are listed too |
| `open_project` | `create_project` | `create=true` makes the folder first |
| `build_project` | `get_build_status`, `cancel_build` | `action` is `build`, `status` or `cancel` |
| `export_build` | `get_build_report` | `report=true` or a `jobId` reads a run back |
| `run_standalone` | `stop_standalone` | `stop=true` |
| `get_project_info` | `get_engine_repo` | `engineRepo=true` |
| `reload_game_code` | `set_auto_reload` | `autoReload` sets the switch and reloads nothing |
| `get_code_project` | `list_actor_classes` | `actorClasses` is `engine`, `project` or `all` |
| `cookie_jars` | `list_cookie_jars`, `add_cookie_jar`, `refresh_cookie_jar` | no arguments lists; `add`, `refresh` |
| `search_cookies` | `list_installed_cookies` | `installed=true` |

`undo` and `redo` stayed apart: they are one word each, and an agent that means one and calls
the other undoes real work.

`apply_scene_edits` dispatches its ops by tool name, so the batch vocabulary merged with the
tools rather than keeping the old names alive: its ops are exactly `spawn_actor`,
`duplicate_actor`, `destroy_actor`, `set_transform`, `set_actor`, `set_properties`,
`set_material`, `add_component`, `remove_component` and `attach_actor`. An op name is always a
tool that can also be called on its own.

| Tool | Description | Parameters |
|---|---|---|
| `add_component` | Add a component to an actor by type name (short names such as 'Light3D' work). Companion components the type requires are added automatically and reported. properties sets initial values by property name. | `actor`: string<br>`componentType`: string<br>`properties`: any JSON value (optional) |
| `apply_scene_edits` | Run several scene edits in one call and one undo step. ops is a JSON array; each op is a tool's arguments plus "op", one of: spawn_actor, duplicate_actor, destroy_actor, set_transform, set_actor, set_properties, set_material, add_component, remove_component, attach_actor. An actor argument may be "$n": the id spawned by op n (0-based). | `ops`: any JSON value<br>`stopOnError`: boolean (default false) |
| `ask_user` | Ask the user a question in the editor and wait for the answer. choices become buttons; the user can also type one unless allow_free_text is false. Blocks until answered, or returns a timeout or cancelled status. Use it for decisions that are expensive to change later, not for routine ones. | `question`: string<br>`choices`: array of string (optional)<br>`allowFreeText`: boolean (default true)<br>`timeoutSeconds`: integer (default 900) |
| `attach_actor` | Attach an actor to a parent, so it moves, rotates and scales with it and is destroyed with it. Both the 2D and 3D transforms follow. Omit parent to detach the actor to the scene root, keeping its world position; children=true detaches its children instead. | `actor`: string<br>`parent`: string (optional)<br>`keepWorldTransform`: boolean (default true)<br>`children`: boolean (default false) |
| `bake_cookie` | Save reusable work from the open project back into the CookieJar as a new cookie. Give it the files, a one-line summary, and agent instructions saying how to wire it up in the next game. Do this whenever a task produces something a second game would want. | `id`: string<br>`name`: string<br>`summary`: string<br>`agentInstructions`: string<br>`files`: array of string<br>`tags`: array of string (optional)<br>`nextSteps`: array of string (optional)<br>`requires`: array of string (optional)<br>`version`: string (default "1.0.0")<br>`jar`: string (optional)<br>`overwrite`: boolean (default false)<br>`dryRun`: boolean (default false) |
| `build_project` | Compile the project's C# code and return structured diagnostics. Building alone does not change the editor — call reload_game_code, which builds for you, to make the new code live. After editing engine source call rebuild_engine_and_restart instead. A slow build returns a build_id: action 'status' reads it back (the latest without one, and any build started by another tool), 'cancel' stops it. For an engine rebuild, state 'restarting' means the editor is about to restart — stop calling tools, wait 15-30 seconds, then get_context. | `action`: string (default "build")<br>`buildId`: string (optional)<br>`waitSeconds`: integer (default 40) |
| `capture_viewport` | A PNG screenshot of the editor viewport as it is rendered right now, its longest edge width px. 640 is enough to judge a scene and costs about a third of 1024; use 1024 only to read text. includeUi captures the whole editor window with its panels instead. Give position to render width x height from a camera pose of your choosing (with lookAt or rotation [pitch, yaw, roll] degrees) without moving any camera. | `width`: integer (default 640)<br>`height`: integer (default 360)<br>`includeUi`: boolean (default false)<br>`position`: array of number (optional)<br>`lookAt`: array of number (optional)<br>`rotation`: array of number (optional)<br>`fov`: number (default 60) |
| `console` | Read the editor's Output Log. Pass the latestSequence from the previous result as sinceSequence to get only new entries; level filters to that severity and above, info, warning or error. message instead writes a line, prefixed [Claude], at level. clear=true empties the log. | `sinceSequence`: integer (default 0)<br>`level`: string (optional)<br>`contains`: string (optional)<br>`limit`: integer (default 50)<br>`message`: string (optional)<br>`clear`: boolean (default false) |
| `cookie_jars` | The jars the catalogue is built from, and whether each may be installed from. add proposes a new jar at a git URL or folder — nothing is cloned and nothing is trusted: the person at the editor approves it in the Cookie Jar panel, so tell them what you proposed. refresh fetches a trusted git jar by name and reports what changed, including any cookie this project has installed. | `add`: string (optional)<br>`name`: string (optional)<br>`refresh`: string (optional) |
| `create_class` | Generate a starter C# file for a component, actor, gamemode, playercontroller, character or tool (a static class with an [McpTool] method) under Source/<kind folder>/<Name>.cs in the project's namespace. Returns the path; edit it with your file tools, then call reload_game_code. Never overwrites an existing file. Creates the C# project first when the project has none. | `kind`: string<br>`name`: string<br>`namespace`: string (optional)<br>`folder`: string (optional) |
| `create_code_project` | Add a C# project to the open SexyBiscuit project: the csproj, Source/ with starter classes, a per-machine props file pointing at this engine, and .gitignore. Existing files are never overwritten unless overwrite is true. It then builds, hot-loads the assembly and puts the project's own GameMode in the scene. | `overwrite`: boolean (default false)<br>`build`: boolean (default true)<br>`swapGameMode`: boolean (default true) |
| `describe_components` | The component types that can be added. Without type: names grouped by category (Rendering, Physics, Gameplay, Audio, Animation, AI, UI, Scripting…), or with namesOnly=false a one-line description, required companions and engine/project source for each. With type: that one type's editable properties — name, type, enum values, default, documentation, and whether it is saved in the scene file. | `type`: string (optional)<br>`category`: string (optional)<br>`search`: string (optional)<br>`namesOnly`: boolean (default true) |
| `destroy_actor` | Remove an actor from the scene. Undo brings it back. | `actor`: string |
| `duplicate_actor` | Clone an actor with all of its components and properties, offset by delta world units (pixels for 2D). The copy is placed in the same layer and becomes selected. | `actor`: string<br>`newName`: string (optional)<br>`offset`: array of number (optional) |
| `editor_camera` | The editor camera and the viewport, read with no arguments. position, lookAt or rotation [pitch, yaw, roll] degrees move the editor camera (not any scene camera); focus selects an actor and frames it. view3d switches between the 3D pipeline and the 2D sprite pass, gameCamera looks through the scene's MainCamera3D instead, and fullscreen shows the game over the whole window while playing. | `position`: array of number (optional)<br>`lookAt`: array of number (optional)<br>`rotation`: array of number (optional)<br>`focus`: string (optional)<br>`view3d`: boolean (optional)<br>`gameCamera`: boolean (optional)<br>`fullscreen`: boolean (optional) |
| `export_build` | Export the open project from disk: stage per target, publish self-contained desktop players (needs the engine source and the .NET SDK), archive them, and optionally upload the native archives to DarksGames (needs DG_BUILD_TOKEN). Save the scene first. Returns one line per target with the archive, its size, and the upload URL or the first errors; a longer run returns a job id. report=true, or a jobId, reads an earlier run's report back instead of starting one. | `platforms`: array of string (optional)<br>`configuration`: string (optional)<br>`publish`: boolean (default true)<br>`upload`: boolean (default false)<br>`version`: string (optional)<br>`waitSeconds`: integer (default 120)<br>`report`: boolean (default false)<br>`jobId`: string (optional) |
| `find_actors` | Filter the open scene's actors by name substring, tag, component type, layer or class. Filters are optional and combine with AND. | `nameContains`: string (optional)<br>`tag`: string (optional)<br>`componentType`: string (optional)<br>`layer`: string (optional)<br>`class`: string (optional) |
| `get_actor` | One actor, by id or exact name. detail 'full' (default): transform in degrees, every component with its editable properties, bounds, selection; 'row': id, name, class, layer, tag, component types, position. property reads a single value instead, keyed 'Type.Property' such as 'Light3D.Intensity' or 'Actor.Tag'. | `actor`: string<br>`detail`: string (default "full")<br>`property`: string (optional) |
| `get_code_project` | Describe the open project's C# code project: csproj path, Source/ files, output DLL, the loaded assembly generation, whether it was compiled against the engine build this editor runs, and the last build. Read-only; use create_code_project to add one. actorClasses 'engine', 'project' or 'all' adds the Actor subclasses spawn_actor's class accepts, with base class and doc summary. | `actorClasses`: string (optional) |
| `get_context` | Where things stand, in about a hundred tokens: project, scene (name, path, actor count, layers, dirty flag, checks), selection, play state, C# project, last build and the last CI run on main. Call this first; get_scene_summary, get_actor and get_project_info give more when a task needs it. | — |
| `get_cookie` | Everything about one cookie: its manifest, what it provides, every file it would install, and its full AGENT.md instructions. | `id`: string |
| `get_material` | Read a MeshRenderer material. | `actor`: string<br>`materialIndex`: integer (default 0) |
| `get_project_info` | Describe the open project and the editor: root folder, asset/script/scene folders, the open scene and whether it has unsaved changes, play mode, the MCP URL, and the templates open_project accepts with descriptions. get_context is the cheap version; use this for the folders and the template list. engineRepo=true adds where the engine source is, its branch, and the dotnet commands that build it — read that before editing engine code. | `engineRepo`: boolean (default false) |
| `get_scene_summary` | The open scene. format 'compact' (default): a header (layers, dirty flag, checks for camera, light, player start, game mode) then one text line per actor — id, name, class, layer, tag, components, position. 'json': the same as JSON. 'view': every actor's editable component properties (rotations in degrees, colours as hex). 'file': the exact .scene JSON save_scene would write. Page with offset and limit. Ids change after undo, redo or load. | `format`: string (default "compact")<br>`layer`: string (optional)<br>`offset`: integer (default 0)<br>`limit`: integer (default 100)<br>`includeComponents`: boolean (default true) |
| `get_session_usage` | This session's token meter, about seventy tokens: turns, context per API call, cache share, output tokens, the size of the tool results, cost, the last turn, and the tools that returned the most. Read it to see what a task cost before repeating the pattern. | `topTools`: integer (default 5) |
| `install_cookie` | Copy a cookie into the open project, with anything it requires, then build and hot-reload if it added C#. The result carries the cookie's AGENT.md and its next steps: follow those rather than reading its files. Nothing is written when the plan is blocked; read the conflicts and fix them. | `id`: string<br>`includeDependencies`: boolean (default true)<br>`dryRun`: boolean (default false)<br>`overwrite`: boolean (default false) |
| `list_assets` | Files under the project's asset directories and its Scenes folders, each with a type: texture, audio, model, script, scene, font or other. The open scene is marked. type filters to one of those — type 'scene' is the list of .scene files. | `subdirectory`: string (optional)<br>`extensions`: array of string (optional)<br>`type`: string (optional)<br>`limit`: integer (default 100) |
| `load_scene` | Load a .scene file — path relative to the project root, extension optional — and make it the open scene. Refused during play mode. Unsaved changes are lost (undo can bring them back). | `path`: string |
| `new_scene` | Replace the open scene with a fresh one. template 'default3d' gives a sky, two lights, a floor, a few shapes, a player start and a game mode; 'default2d' a 2D camera; 'empty' just the default layers. Unsaved changes are lost (undo can bring the previous scene back). | `name`: string (default "Untitled")<br>`template`: string (default "default3d") |
| `open_project` | Open a project from its .sbproject file, or from a folder that contains one; its default scene is loaded when the file exists. create=true instead creates the project at that folder path (the last segment is its name) from a template — names come from get_project_info — and opens it. | `path`: string<br>`create`: boolean (default false)<br>`template`: string (optional) |
| `play_mode` | Drive play mode and read its state: playing and paused flags, fps, frame count, time scale and the scene name. action 'play' starts it (F5; the scene is snapshotted and changes made while playing are discarded), 'stop' ends it and restores the scene (F7), 'pause', 'resume' and 'toggle' do the obvious, 'step' advances frames frames of 1/60 s while paused, and 'status' (the default) only reads. | `action`: string (default "status")<br>`frames`: integer (default 1) |
| `publish_build` | Publish archives that already exist to DarksGames — no rebuild, so a twenty-minute desktop publish is not repeated just to send the file. Native builds only; the web build is skipped. Re-publishing the same slug, version and platform replaces that build in place, so bump version for a genuinely new one. Needs DG_BUILD_TOKEN or the first line of ~/.sexybiscuit/dg-token. | `platforms`: array of string (optional)<br>`channel`: string (optional)<br>`notes`: string (optional)<br>`requirements`: string (optional)<br>`appSlug`: string (optional)<br>`version`: string (optional)<br>`hidden`: boolean (default false)<br>`replace`: boolean (default true)<br>`archive`: string (optional)<br>`waitSeconds`: integer (default 900) |
| `rebuild_engine_and_restart` | Rebuild the engine and editor from source and restart, so engine changes take effect. A failed build leaves the running editor untouched and returns diagnostics. On success it restarts within seconds and reopens the same project, scene and session: stop calling tools, wait 15-30 seconds, then call get_context until it answers. Never repeat the rebuild. | `configuration`: string (optional)<br>`runTests`: boolean (default false)<br>`waitSeconds`: integer (default 40) |
| `redo` | Redo N undone changes. Actor ids are regenerated. | `steps`: integer (default 1) |
| `reload_game_code` | Build the C# project and hot-reload the assembly into the running editor, keeping the scene and its unsaved edits. Play mode is stopped first. Reports which classes and game_ tools appeared or disappeared. Refuses when the game was built against a different engine than this editor runs — rebuild_engine_and_restart then. autoReload turns automatic build and reload on save in Source/ on or off and reloads nothing itself; it is off by default, so you normally call this tool explicitly. | `build`: boolean (default true)<br>`stopPlayMode`: boolean (default true)<br>`allowEngineMismatch`: boolean (default false)<br>`waitSeconds`: integer (default 40)<br>`autoReload`: boolean (optional) |
| `remove_component` | Remove the first component of a type from an actor. The 2D Transform cannot be removed; remove a Transform3D only if nothing else on the actor needs it. | `actor`: string<br>`componentType`: string |
| `run_scene_report` | Play the open scene for a few seconds and report what happened in about a hundred tokens: frames and fps, script errors, console warnings and errors (deduplicated, newest last) and the actor count at the end. Play mode is exited and the scene restored afterwards. Use it in place of play_mode, waiting and console. | `seconds`: number (default 3)<br>`scene`: string (optional)<br>`maxLines`: integer (default 10) |
| `run_standalone` | Build the project (a full build, engine included) and launch the game as its own process with the project root as working directory, using ProjectSettings.json and its StartScene. Its output streams into the Output Log tagged [Game]. A previous instance is stopped first; stop=true only stops the running one. | `waitSeconds`: integer (default 120)<br>`stop`: boolean (default false) |
| `run_tests` | Run a test suite and return the totals and the failing names, not the log. An html5 filter naming files ('ui*') runs just those; anything else matches test names. The first run after a change includes a build. | `project`: string (default "engine")<br>`filter`: string (optional)<br>`waitSeconds`: integer (default 600) |
| `save_scene` | Save the open scene to disk. Omit path to save where it was loaded from or last saved; otherwise give a project-relative path such as 'Scenes/Level1.scene'. Refused during play mode; refuses paths outside the project. | `path`: string (optional) |
| `say` | Show a message to the user in the editor's Assistant panel. For sessions driving the editor from a terminal, this is how the user reads you; the embedded assistant's own replies already appear there and need not call it. | `message`: string<br>`level`: string (optional) |
| `search_cookies` | Search the CookieJar: the team's library of ready-made modules (a character controller, an input map, a HUD). Call this before writing a common mechanic by hand. Returns one line per cookie with what it provides; install_cookie then copies one into the open project and tells you how to wire it up. installed=true instead lists what this project has installed, and any file edited or deleted since. | `query`: string (optional)<br>`tags`: array of string (optional)<br>`engine`: string (optional)<br>`includeInstalled`: boolean (default true)<br>`limit`: integer (default 20)<br>`installed`: boolean (default false) |
| `select_actor` | Select an actor in the editor so it shows in the Details panel and wears the gizmo. Omit actor to read the current selection; clear=true deselects. | `actor`: string (optional)<br>`clear`: boolean (default false) |
| `set_actor` | Set an actor's name, tag, active flag, scene layer (a draw-order group, created if missing), physics layer (the integer Actor.Layer used by collision masks) or lifeSpan in seconds (0 = forever). | `actor`: string<br>`name`: string (optional)<br>`tag`: string (optional)<br>`active`: boolean (optional)<br>`layer`: string (optional)<br>`layerOrder`: integer (default 0)<br>`physicsLayer`: integer (optional)<br>`lifeSpan`: number (optional) |
| `set_material` | Set a MeshRenderer material: albedo colour, metallic 0-1, roughness 0-1, emissive intensity, and texture paths relative to the project root. Only the given fields change. The material is saved with the scene. | `actor`: string<br>`albedoColor`: colour '#RRGGBB[AA]' or name (optional)<br>`metallic`: number (optional)<br>`roughness`: number (optional)<br>`emissiveIntensity`: number (optional)<br>`albedoTexture`: string (optional)<br>`normalTexture`: string (optional)<br>`materialIndex`: integer (default 0) |
| `set_properties` | Set several properties on one actor at once. Keys are 'ComponentType.Property' (or 'Actor.Property'), e.g. {"Light3D.Intensity": 2, "Transform3D.Position": [0, 3, 0]}. Valid entries are applied even if others fail. | `actor`: string<br>`properties`: any JSON value |
| `set_transform` | Set or change position, rotation and/or scale. 3 elements address the Transform3D (added if missing) — rotation is [pitch, yaw, roll] in degrees; 2 elements address the 2D transform — rotation is [degrees]. relative=true adds position and composes rotation instead of replacing them; space 'local' works along the parent's axes (relative: the actor's own). lookAt or lookAtActor aims a 3D actor's forward axis (-Z, where cameras and lights point) instead of taking a rotation. | `actor`: string<br>`position`: array of number (optional)<br>`rotation`: array of number (optional)<br>`scale`: array of number (optional)<br>`lookAt`: array of number (optional)<br>`lookAtActor`: string (optional)<br>`relative`: boolean (default false)<br>`space`: string (default "world") |
| `spawn_actor` | Create an actor and select it. Give one of: shape, a built-in mesh with its own coloured material (Cube, Sphere, Plane, Quad, Cylinder, Cone, Capsule, Torus — unit-sized, use scale); preset, a palette entry (Camera, Point Light, Character…); class, an Actor subclass such as 'GameMode'; or components, bare type names (required companions are added). position/rotation/scale with 3 elements make a 3D actor (rotation is [pitch, yaw, roll] degrees), 2 elements a 2D one. properties sets initial values keyed 'Type.Property'. list=true returns the preset palette instead of spawning. | `name`: string (optional)<br>`shape`: string (optional)<br>`preset`: string (optional)<br>`class`: string (optional)<br>`components`: array of string (optional)<br>`position`: array of number (optional)<br>`rotation`: array of number (optional)<br>`scale`: array of number (optional)<br>`layer`: string (optional)<br>`tag`: string (optional)<br>`properties`: any JSON value (optional)<br>`color`: colour '#RRGGBB[AA]' or name (optional)<br>`metallic`: number (optional)<br>`roughness`: number (optional)<br>`transform3d`: boolean (default true)<br>`list`: boolean (default false) |
| `spawn_many` | Spawn one built-in shape (Cube, Sphere, Plane, Quad, Cylinder, Cone) or one palette preset at several positions, or on a grid. Returns ids, names and positions only. | `what`: string<br>`positions`: array of array of number (optional)<br>`grid`: array of integer (optional)<br>`spacing`: number (default 2)<br>`origin`: array of number (optional)<br>`name`: string (optional)<br>`scale`: array of number (optional)<br>`color`: string (optional)<br>`layer`: string (optional)<br>`tag`: string (optional) |
| `undo` | Undo the last N scene changes made through these tools (also Edit > Undo in the editor). The scene is restored from a snapshot, so actor ids are regenerated — re-query them. | `steps`: integer (default 1) |
| `uninstall_cookie` | Remove a cookie from the open project. A file is deleted only when it still matches what was installed, so anything edited since is kept and reported. | `id`: string<br>`force`: boolean (default false) |
| `wait_for_user` | For sessions driving the editor from a terminal: block until the user types in the editor's Assistant panel, then return what they typed. Call it at the end of every turn and act on the result. Also returns a timeout, cancelled or editor_closing status, or not_needed for the embedded assistant. | `timeoutSeconds`: integer (default 0) |

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

Six of the tools are the module library: `search_cookies` (with `installed=true`
for what this project already has), `get_cookie`, `install_cookie`,
`uninstall_cookie`, `bake_cookie` and `cookie_jars`. The assistant is told to
search the jar before writing a common mechanic by hand, and to bake reusable
work back when a task produces something a second game would want.

Installing compiles and runs the cookie's code, so a jar that is not the engine's
own has to be trusted by a person: `cookie_jars` with `add` records an address and
never clones, and installing from a non-builtin jar raises a question in the editor.
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
in Place Actors, Add Component, `get_code_project`'s `actorClasses` and the serialiser, and `[McpTool]`
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
fails the run when the compact JSON is longer than N characters; CI and
`SexyBiscuit.Editor.Tests` both hold the full catalogue under 41,000 characters (40,433 today,
about 10,100 tokens, the price a session pays once when Claude Code loads the tool set). The
budget only ever goes down: lower it when tools merge, never raise it to make the build pass.

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
