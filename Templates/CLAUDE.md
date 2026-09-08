# Templates — fifteen complete starter projects

Each folder is a whole project: `template.json` (name, description, category),
`ProjectSettings.json`, `Scenes/*.scene`, `Scripts/*.js`. No C# anywhere: a template runs
unchanged on both engines and is the scripting contract's test corpus.

## Gate for this folder

    cd html5 && npm run validate                                                                     # every template: scenes load, scripts compile, members in the contract
    cd html5 && node --test tests/templates.test.js                                                  # every template's scripts tick sixty frames in the browser engine
    dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~TemplateTests"   # the same under Jint

## Rules that apply here only

- Only members of `html5/src/scripting/bridge-api.json`; the validator names the line outside it.
- A new template is a folder plus its name in `.claude/skills/new-game/SKILL.md`; `template.json`
  is what the launcher lists.
- `Hello World` is CI's export smoke on both pipelines; keep it tiny.
- Leave no build output here: `.sexybiscuit/` and `dist/` are ignored, but they bloat every search.
