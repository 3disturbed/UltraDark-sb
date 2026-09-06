# 28. The CookieJar

Project: `SexyBiscuit.Engine/CookieJar` (format, catalogue, install, bake) ·
`SexyBiscuit.Engine/Mcp/Tools/CookieTools.cs` (the tools) ·
`SexyBiscuit.Editor/CookieJar` (host, panel, trust) · **cross-platform**

```bash
dotnet run --project SexyBiscuit.Editor -- --dump-mcp-tools --markdown | grep cookie
```

Every game starts from nothing: the character controller, the input map, the scoring loop typed
again into a project nobody else can reuse. The CookieJar is where that work goes instead. It is a
library of modules, catalogued so that an agent finds one in a single call, installs it into the
open project, and is told in the same result how to wire it up.

There are no prices and no storefront — the engine is in-house, so a jar is just a folder or a git
repository of modules, each carrying its own instructions.

- A **cookie** is one module: some source, whatever assets and scene fragments it needs, and an
  `AGENT.md`.
- A **jar** is a source of cookies. The engine repository's own `CookieJar/`, a folder you point at,
  or a git repository cloned into your library.
- The **catalogue** is every enabled jar, merged. The first jar to supply an id wins, so a jar
  listed before the builtin one deliberately overrides it.

---

## What a cookie looks like

```
<jar>/<id>/
  cookie.json      id (kebab-case, matching the folder), name, version, summary, engines, provides
  AGENT.md         required: what it gives you and how to wire it up
  README.md        optional, for people
  Source/          C#, in namespace Cookies.<PascalId>   -> <project>/Source/Cookies/<PascalId>/
  Scripts/         JavaScript                            -> <project>/Scripts/Cookies/<id>/
  Scenes/          .scene and .prefab fragments          -> <project>/Scenes/Cookies/<id>/
  Assets/          textures, audio, models               -> <project>/Assets/Cookies/<id>/
  Config/          json the AGENT.md refers to           -> <project>/Config/Cookies/<id>/
```

Those five destinations are the convention. A `files` list in the manifest overrides or extends it,
and a cookie that fits the convention leaves it out.

`provides` names the components, actor classes, scripts and prefabs the cookie adds. It is not
decoration: it makes the catalogue searchable without opening a single source file, and it is how a
clash between two cookies exporting the same component name is caught before anything is written.

### Namespaces are fixed, never substituted

A cookie's C# is authored in `Cookies.<PascalId>` and copied byte for byte. The generated game
csproj globs `Source/**/*.cs`, so any namespace under `Source/` compiles and there is no technical
need to match the project's own.

The reason it matters is uninstalling. A byte-identical copy means a hash comparison proves the
author has not edited a file, which is what lets a removal delete its own work and keep theirs.
Substitution would destroy that, and doing it correctly needs a parser rather than a regex.

### Asset paths are rewritten on the way in

A cookie refers to its own texture as `Assets/brick.png`. Installed, that file lives at
`Assets/Cookies/<id>/brick.png`, and every reference to it is repointed. The rewrite walks the JSON
and changes any property whose name ends in `Path` whose value the install is moving, so it reaches
the material map paths nested inside a mesh renderer's materials, works for a component type the
project has not compiled yet, and cannot touch a value it is not moving. Baking inverts the same
map.

---

## Installing

From the editor: **View ▸ Cookie Jar**, pick one, press Install. From the assistant, or any MCP
client: `search_cookies`, then `install_cookie`.

Installing plans first and writes nothing until the plan is clean. A plan is refused when a
destination would escape the project or land in build output, when a file is already there and
differs, when another installed cookie already provides one of the same type names, when a required
cookie is in no enabled jar, or when the jar has not been trusted. Files are staged in the
project's scratch folder, rewritten there, then moved into place, keeping a copy of anything they
replace — so a failure half way leaves the project as it was.

What was installed is recorded in **`CookieJar.lock.json`** at the project root, with a hash for
every file. That file is meant to be committed: it is not in `.sexybiscuit/`, which is gitignored
and would lose the record the moment somebody cloned the game.

Uninstalling deletes a file only when it still hashes to what was installed. Anything edited since
is yours, and is kept and reported.

---

## Baking

**Tools ▸ Bake Cookie from this Project**, or `bake_cookie`. Pick the files, write one line of
summary and a few lines of instructions, and it writes a cookie into the jar: manifest, `AGENT.md`,
`README.md`, the files with their asset references inverted, and the source moved into the cookie's
namespace.

What the cookie provides is derived by reading its C#, and any namespace move is reported. Both are
best effort and both are meant to be checked: the manifest is what everything downstream trusts.

By default a baked cookie lands in the engine repository's `CookieJar/`, so it is reviewed and
shipped like a template. With no checkout it falls back to your own library under the SexyBiscuit
settings folder.

---

## Jars, and why a git one has to be trusted

Installing a cookie compiles and runs its code, and the assistant runs in a permission mode where
nothing prompts. So the gate is not in the assistant, it is in the shape of the tools:

| Jar | Trusted because |
|---|---|
| `builtin` — the engine repository's `CookieJar/` | it is reviewed with the engine |
| a folder you added in the panel | you chose it |
| a cloned git repository | **only when you press Trust and clone** |

`add_cookie_jar` records an address and returns `awaiting_approval`. It has no code path that
clones, enables or trusts anything, so there is no flag for a model to set. The clone happens in
the panel, when a person presses the button. Installing from any jar that is not the builtin one
also raises a question on the same board that backs `ask_user`, and a refusal is an error rather
than a warning.

Trust is stored per user, never in a project, so a repository you clone cannot ship a file that
pre-trusts somebody else's jar. Clones are shallow, without tags or submodules, and git runs with
its credential prompts disabled so a private repository fails in a second instead of blocking the
editor on a prompt nobody can see.

---

## The tools

| Tool | What it does |
|---|---|
| `search_cookies` | Ranked search over id, name, tags and summary; returns what each cookie provides |
| `get_cookie` | One cookie in full, including its `AGENT.md` and every file it would install |
| `install_cookie` | Plans, applies, builds and reloads; returns the instructions and the next steps |
| `uninstall_cookie` | Removes what it installed, keeps what you edited |
| `list_installed_cookies` | The lock file, plus any file that has drifted from it |
| `bake_cookie` | Writes a new cookie out of the open project |
| `list_cookie_jars` | The jars, and whether each may be installed from |
| `add_cookie_jar` | Records a jar for approval. Never clones |
| `refresh_cookie_jar` | Fetches a trusted git jar and says what changed |

`install_cookie` returns the namespace and `using` line, what the cookie provides, every file
written, the build outcome, the next steps, and the whole `AGENT.md`. That shape is deliberate: the
call after an install should need no reads.

Two resources go with them: `sexybiscuit://cookies` is the catalogue as a table, and
`sexybiscuit://cookies/installed` is the lock file. `get_project_info` also grows a small `cookies`
section, so the first call an agent makes already says what the library holds.

---

## Writing a good AGENT.md

It is the whole point of the cookie, and it is what the installer reads instead of the source. Say:

- **How to turn it on** in one step, with the line of code or the component to add.
- **What it adds**, by name.
- **What to set**, and what the sensible values are.
- **What it deliberately does not do**, so nobody goes looking for it.

Keep it short. It is copied into a context window every time somebody installs the cookie.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| A cookie is missing from the catalogue | Its manifest failed to parse or validate. `list_cookie_jars` reports the problem; a broken cookie never blanks the jar it sits in |
| "id does not match its folder name" | The folder name is how a jar addresses a cookie; rename one to match |
| "AGENT.md is required" | Every cookie ships instructions. A cookie without them is not installable |
| Install refused with `UntrustedJar` | Approve the jar in the Cookie Jar panel. No tool can do this |
| Install refused with `TypeNameCollision` | Two cookies export the same component short name, which a scene cannot tell apart. Rename one, or do not install both |
| Uninstall left files behind | They no longer hash to what was installed, so they are treated as yours. Pass force if you really mean it |
| A cookie's assets did not follow it | Its scene refers to a file the cookie does not ship. `install_cookie` lists those under `unresolvedReferences` |

---

## Next

- [25. AI Assistant & MCP](25-ai-assistant-mcp.md) — the tools, and the sessions that call them
- [12. Scenes & Prefabs](12-scenes-prefabs.md) — the format a cookie's scene fragments are in
