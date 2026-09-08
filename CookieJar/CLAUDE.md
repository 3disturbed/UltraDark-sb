# CookieJar — reusable modules that carry their own instructions

A cookie is a folder: `cookie.json` (id, summary, engines, requires, provides, nextSteps),
`AGENT.md` (why it exists, wiring, the traps, tuning, what it does not do) and either
`Scripts/*.js` (`"engines": ["js"]`, runs on both engines unchanged) or `Source/*.cs`
(`["csharp"]`, native only). `README.md` here lists them; the editor's Cookie Jar panel and the
`search_cookies`, `install_cookie` and `bake_cookie` tools are the same library.

## Gate for this folder

- A JS cookie: install it into a throwaway project exactly as its `AGENT.md` says, then from
  `html5/`: `npm run validate -- <that project> --strict`. A cookie whose own instructions do not
  validate is worse than no cookie.
- A C# cookie: `SEXYBISCUIT_SLOW_TESTS=1 dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~BundledCookieTests"`
  compiles every C# cookie against this engine; CI runs it on every push.

## Rules that apply here only

- Check `engines` first: a `csharp` cookie is no use in a phase 1 prototype, which is where every
  game starts.
- Edit the jar and reinstall; never edit the copy installed into a project.
- Bump `version` in `cookie.json` when the scripts change, `engineVersion.min` when the contract
  they need does.
- `AGENT.md` is read by an agent with thirty tokens for the summary and a few hundred for the
  wiring. Keep the order: why, wiring, the traps, tuning, what it does not do.
- Reference: `wiki/28-the-cookiejar.md`.
