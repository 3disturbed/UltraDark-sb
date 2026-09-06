using System.Text;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.CookieJar;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>The CookieJar as documents a client can read without calling a tool.</summary>
public sealed class CookieResources
{
    private readonly ICookieHost _host;

    public CookieResources(ICookieHost host) => _host = host;

    [McpResource("sexybiscuit://cookies", "Cookie catalogue", "text/markdown",
        Description = "Every module in the CookieJar: what it is, what it provides, and whether it is installed.")]
    public string Catalogue()
    {
        var catalogue = _host.Catalogue();
        var installed = _host.Lock;
        var text      = new StringBuilder("# The CookieJar\n\n");

        if (catalogue.All.Count == 0)
        {
            text.AppendLine("No cookies. Bake one with `bake_cookie`, or add a jar in the editor's Cookie Jar panel.");
            return text.ToString();
        }

        text.AppendLine("Reusable modules. `install_cookie` copies one into the open project and returns its");
        text.AppendLine("instructions; `bake_cookie` saves new work back here.").AppendLine();

        foreach (var group in catalogue.All.GroupBy(c => c.JarName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var jar = catalogue.Jar(group.Key);
            text.Append("## ").Append(group.Key);
            if (jar is { IsInstallable: false }) text.Append(" (not trusted, so not installable)");
            text.AppendLine().AppendLine();

            text.AppendLine("| Cookie | Version | Engines | Provides | Summary |");
            text.AppendLine("|---|---|---|---|---|");

            foreach (var cookie in group.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                var m = cookie.Manifest;
                string provides = string.Join(", ", m.Provides.TypeNames.Concat(m.Provides.Scripts));
                string mark     = installed.Find(m.Id) != null ? " *(installed)*" : "";

                text.Append("| `").Append(m.Id).Append('`').Append(mark)
                    .Append(" | ").Append(m.Version)
                    .Append(" | ").Append(string.Join(", ", m.Engines))
                    .Append(" | ").Append(Escape(provides.Length == 0 ? "-" : provides))
                    .Append(" | ").Append(Escape(m.Summary))
                    .AppendLine(" |");
            }

            text.AppendLine();
        }

        if (catalogue.Problems.Count > 0)
        {
            text.AppendLine("## Problems").AppendLine();
            foreach (var problem in catalogue.Problems)
                text.Append("- ").Append(problem.Severity).Append(": ").Append(problem.Subject)
                    .Append(" — ").AppendLine(problem.Message);
        }

        return text.ToString();
    }

    [McpResource("sexybiscuit://cookies/installed", "Installed cookies", "application/json",
        Description = "What the open project has installed, from CookieJar.lock.json.")]
    public string Installed()
    {
        var rows = new JsonArray();
        foreach (var entry in _host.Lock.Cookies)
            rows.Add(new JsonObject
            {
                ["id"]        = entry.Id,
                ["version"]   = entry.Version,
                ["jar"]       = entry.Jar,
                ["namespace"] = entry.Namespace,
                ["files"]     = entry.Files.Count,
            });

        return new JsonObject { ["installed"] = rows }.ToJsonString(McpJson.Indented);
    }

    [McpPrompt("use_a_cookie", "Find a module in the CookieJar for a mechanic, and wire it up.")]
    public string UseACookie([McpParam("The mechanic you want, in a few words")] string mechanic)
        => $"Search the CookieJar for a module that gives this project {mechanic}. Call search_cookies first; if there "
         + "is a good match, install_cookie it and follow the AGENT.md the result returns, checking your work with "
         + "capture_viewport. If there is nothing suitable, build it, and then bake_cookie it back so the next game "
         + "gets it for free. Say which you did and why.";

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}
