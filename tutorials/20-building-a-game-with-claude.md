# Tutorial 20 — Building a Game with Claude

**You will build:** a small arena level and a spinning-pickup behaviour, by describing them
to Claude inside the editor: watching it work, answering one question, stopping it once,
undoing a change, driving the same editor from a terminal, and finally having it change the
engine itself.

**Time:** 30 minutes · **Needs:** the editor building ([Tutorial 17](17-editor-workflow.md))
and Claude Code installed and signed in.

Reference: [wiki 25. AI Assistant & MCP](../wiki/25-ai-assistant-mcp.md).

---

## 1. Check the install

```bash
dotnet run --project SexyBiscuit.Editor -- --assistant-selftest --dry-run
```

This lists every `claude` binary the editor can find and whether it answers `--version`:

```
Claude Code candidates:
  [--] /opt/homebrew/bin/claude  (PATH) - exit 1: claude native binary not installed
  [ok] /Users/you/.local/bin/claude  (native installer) - 2.1.260 (Claude Code)
Using: /Users/you/.local/bin/claude (2.1.260 (Claude Code))
```

At least one `[ok]` line is what you need. If there is none, install Claude Code
(`curl -fsSL https://claude.ai/install.sh | bash`, or `irm https://claude.ai/install.ps1 | iex`
on Windows) and run it once in a terminal to sign in with `/login`. The sign-in is per binary:
the Claude desktop app being signed in does not count.

Drop `--dry-run` to run the whole round trip: the editor's headless MCP server, a real Claude
Code process, one canned conversation, and a PASS/FAIL checklist.

## 2. Open a project and say hello

```bash
dotnet run --project SexyBiscuit.Editor
```

Create a project from the **3D Scene** template (tick **Add C# project**) or open one you have.
The **Assistant** tab sits beside **Details**. Within a couple of seconds its header reads
**Idle** with the model name: the editor started a Claude Code session for this project, and
the transcript's first grey line says so.

Press <kbd>F8</kbd> and type:

> Describe the scene and what it still needs to be playable.

Watch the transcript. Tool rows appear first — `[..] Read project info`, `[..] Read the scene` —
each turning `[ok]` with a duration, then Claude's reply streams in. Click a tool row to see the
exact arguments and result. The **Activity** tab lists the same calls; the **Output Log** shows
nothing for reads, only mutations and failures.

If instead you get **Claude Code is not signed in**: press **Open a terminal to sign in**, type
`/login` in the terminal that appears, then **Resume**.

## 3. Lay out a level by talking

> Build a small arena: a 20 metre floor, four walls, a red cube in the middle, a warm point
> light above it and a camera looking at the cube. Save it as Scenes/Arena.scene.

The viewport fills in as Claude calls `spawn_actor`, `set_material` and
`save_scene`; the outliner gains an actor per call; the viewport shows a purple **CLAUDE**
banner the whole time. When it finishes, the reply summarises what it built and what it would
add next.

Undo works the way it does for your own edits: <kbd>Ctrl</kbd>+<kbd>Z</kbd> reverts one tool
call at a time (Edit › Undo names it). The **Undo last change** quick action asks Claude to do
the same from its side.

## 4. Answer a question

> Delete the walls.

Destroying the user's work needs confirmation, so Claude asks first: a card appears with the
question and buttons. Click **No** and it says so and stops; click **Yes** and the walls go.
Questions come through the `ask_user` tool and wait up to fifteen minutes; Claude is told to
carry on sensibly if you never answer.

## 5. Add C# behaviour

> Add a Spinner component that rotates the cube 90 degrees per second, attach it to the cube,
> and reload the game code.

Claude calls `create_code_project` if the project has no C# yet, then `create_class`, edits the
file with its own tools, and calls `reload_game_code`. The **C# Project** panel shows the build
and the reload ("generation 2, +1 type"); the Details panel now offers `Spinner` in Add
Component. Press <kbd>F5</kbd>: the cube spins. <kbd>F7</kbd> stops and restores the scene.

Open `Source/Components/Spinner.cs` in the Code Editor and you will find ordinary engine code:
a `Component` with a serialisable `DegreesPerSecond` and an `Update` override. Edit it yourself
and ask Claude to reload, or turn on **Auto-reload on save** in the C# Project panel.

## 6. Stop it, and undo

> Fill the arena with 200 pillars in a spiral.

A second or two in, press <kbd>Shift</kbd>+<kbd>F8</kbd> (or **Stop** in the header, or
**Stop Claude** in the toolbar). The banner disappears, the running tool row shows `[--]`, and
the turn ends with an interrupted result. <kbd>Ctrl</kbd>+<kbd>Z</kbd> a few times walks back
the pillars that landed. Tell Claude what you want instead; it picks up from there.

## 7. Drive it from a terminal instead

Stop the embedded session (**Tools › Stop Assistant Session**), then in a terminal:

```bash
cd /path/to/MyGame
claude
```

The project's `.mcp.json` names the editor's server (the editor wrote it when the project
opened), so Claude Code asks once whether to use it. Type in the terminal:

> Say hello in the editor, then wait for me.

The Assistant panel shows **Claude (said) Hello...** and its header says a terminal session is
waiting for you. Type a reply in the panel: it arrives in the terminal as the result of
`wait_for_user`. Everything else works as before, except that Claude's own text stays in the
terminal; it uses `say` for what you should read in the editor.

## 8. Change the engine

Back in the embedded session (press **Start**), ask for something the engine does not have:

> Add a HoverComponent to the engine's Gameplay namespace that bobs an actor up and down on a
> sine wave, with Amplitude and Speed properties, then rebuild the engine and restart.

Claude edits `SexyBiscuit.Engine/Gameplay/`, builds the engine with warnings as errors, and
calls `rebuild_engine_and_restart`. The build log streams into the Output Log; on success the
scene is saved, the window closes and reopens on the same project and scene with the same
selection, and the transcript continues: an **[editor] The SexyBiscuit editor restarted**
line, then Claude confirming `HoverComponent` is available. A failed build changes nothing and
Claude reads the errors instead.

## Checkpoint

You know:

- How the editor finds and signs in a `claude` binary, and how to check it headlessly
- The Assistant panel: transcript, tool rows, Activity, Diagnostics, Stop and Undo
- That Claude asks before destroying your work, and how questions render
- The C# loop: `create_code_project`, `create_class`, `reload_game_code`, and hot-reloaded types in the panels
- The terminal alternative through `.mcp.json`, `say` and `wait_for_user`
- `rebuild_engine_and_restart`, and that the session survives the restart

Costs: every turn ends with a cost line, the header keeps the running total, and
**Settings › Limits** caps a session.

---

**Next:** [Tutorial 18 — Shipping Your Game](18-shipping.md)
