# The CookieJar

Reusable modules for SexyBiscuit games. One folder per **cookie**: some source, whatever assets and
scene fragments it needs, and an `AGENT.md` that says how to wire it up once it is installed.

Install one from the editor's Cookie Jar panel, or ask the assistant — `search_cookies` finds it and
`install_cookie` copies it into the open project, builds it, and hands back the instructions.

## Adding a cookie

The easiest way is to build the thing in a game first, then bake it: Tools ▸ Bake Cookie from this
Project, or `bake_cookie`. That fills in the manifest, moves the source into the cookie's own
namespace and writes a starter `AGENT.md` for you to improve.

By hand, a cookie is:

```
<id>/
  cookie.json      id (kebab-case, matching the folder), name, version, summary, engines, provides
  AGENT.md         required: what it gives you and how to wire it up
  README.md        optional
  Source/          C#, in namespace Cookies.<PascalId>
  Scripts/         JavaScript, against the shared scripting contract
  Scenes/          .scene and .prefab fragments
  Assets/          textures, audio, models
  Config/          json the AGENT.md tells the caller to use
```

Rules worth knowing before you write one:

- **The id must equal the folder name**, and be kebab-case.
- **C# lives in `Cookies.<PascalId>`** and is copied verbatim. Nothing is substituted on install, so
  the file in the jar and the file in the project are byte-identical — which is what lets an
  uninstall tell a file it wrote from a file the author has since edited.
- **Asset references are cookie-relative.** Write `Assets/brick.png`; installing rewrites it to
  wherever the cookie's assets landed in the project.
- **`provides` is load-bearing**, not decoration. It makes the catalogue searchable without opening
  every file, and it is how a clash between two cookies exporting the same component name is caught
  before anything is written.
- **Everything here is compiled in CI** against the engine in this repository, so a cookie that
  stops building is a broken build rather than a surprise for whoever installs it next.
- **A JavaScript cookie declares `"engines": ["js"]`** and puts its scripts in `Scripts/`, written
  against the shared scripting contract like any other game script. There is nothing to compile,
  so the check is the validator instead: drop the scripts into a throwaway project, wire them into
  a scene the way the `AGENT.md` says to, and run
  `npm run validate -- <dir> --strict` from `html5/`. A cookie whose own documentation does not
  validate is worse than no cookie.

Phase 1 is JavaScript-only and it is where every game starts, so a mechanic worth reusing is
usually worth a `js` cookie before it is worth a C# one.

A cookie is one mechanic. A whole game start is a project template, and those live in `Templates/`.
