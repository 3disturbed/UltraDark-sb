using System.Numerics;
using ImGuiNET;
using SexyBiscuit.Engine.CookieJar;

namespace SexyBiscuit.Editor.CookieJar;

/// <summary>
/// The "Cookie Jar" panel: browse the library, install into the open project, manage jars, and
/// bake reusable work back out. It is also the only place a git jar can be trusted and cloned.
/// </summary>
public sealed class CookieJarPanel
{
    private readonly EditorCookieHost _host;

    private string  _search      = "";
    private string? _selectedId;
    private string  _status      = "";
    private DateTime _statusUntil;
    private bool    _busy;

    // Jars tab
    private string _newJar = "";

    // Bake tab
    private string _bakeId       = "";
    private string _bakeName     = "";
    private string _bakeSummary  = "";
    private string _bakeTags     = "";
    private string _bakeAgent    = "";
    private readonly HashSet<string> _bakeFiles = new(StringComparer.Ordinal);
    private string _bakeFilter   = "";

    public CookieJarPanel(EditorCookieHost host) => _host = host;

    /// <summary>Opens the panel on the Bake tab, from the Tools menu.</summary>
    public void RequestBake()
    {
        EditorState.ShowCookieJar = true;
        _tab = Tab.Bake;
    }

    private enum Tab { Browse, Installed, Jars, Bake }

    private Tab _tab = Tab.Browse;

    private void DrawTabStrip()
    {
        foreach (var tab in new[] { Tab.Browse, Tab.Installed, Tab.Jars, Tab.Bake })
        {
            if (tab != Tab.Browse) ImGui.SameLine();

            bool active = _tab == tab;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            if (ImGui.Button(tab.ToString(), new Vector2(88f, 0f))) _tab = tab;
            if (active) ImGui.PopStyleColor();
        }
    }

    public void Draw()
    {
        if (!EditorState.ShowCookieJar) return;

        bool open = EditorState.ShowCookieJar;
        ImGui.SetNextWindowSize(new Vector2(520f, 460f), ImGuiCond.FirstUseEver);

        // A layout saved before this panel existed has no node for it; dock it beside Details
        // rather than throwing the user's arrangement away.
        if (EditorState.DockCookieJarIntoDetails && EditorState.DetailsDockId != 0)
        {
            EditorState.DockCookieJarIntoDetails = false;
            ImGui.SetNextWindowDockID(EditorState.DetailsDockId, ImGuiCond.Always);
        }

        if (!ImGui.Begin("Cookie Jar", ref open))
        {
            ImGui.End();
            EditorState.ShowCookieJar = open;
            return;
        }

        // A hand-rolled tab strip rather than BeginTabBar: the Tools menu opens this panel straight
        // on Bake, and ImGui's programmatic tab selection needs a close flag we do not want.
        DrawTabStrip();

        ImGui.Separator();

        switch (_tab)
        {
            case Tab.Browse:    DrawBrowse();    break;
            case Tab.Installed: DrawInstalled(); break;
            case Tab.Jars:      DrawJars();      break;
            case Tab.Bake:      DrawBake();      break;
        }

        DrawStatus();
        ImGui.End();
        EditorState.ShowCookieJar = open;
    }

    // -------------------------------------------------------------------------
    // Browse
    // -------------------------------------------------------------------------

    private void DrawBrowse()
    {
        var catalogue = _host.Catalogue();

        ImGui.SetNextItemWidth(-90f);
        ImGui.InputTextWithHint("##CookieSearch", "Search the jar", ref _search, 128);
        ImGui.SameLine();
        if (ImGui.Button("Refresh", new Vector2(80f, 0f))) _host.Catalogue(refresh: true);

        if (catalogue.All.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("No cookies yet. Bake one from this project on the Bake tab, or add a jar on the Jars tab.");
            return;
        }

        var hits = catalogue.Search(string.IsNullOrWhiteSpace(_search) ? null : _search, limit: 200);

        ImGui.BeginChild("##CookieList", new Vector2(200f, -1f), ImGuiChildFlags.Border);
        foreach (var hit in hits)
        {
            var installed = _host.Lock.Find(hit.Cookie.Id);
            string label = installed != null ? hit.Cookie.Id + "  *" : hit.Cookie.Id;

            if (ImGui.Selectable(label, _selectedId == hit.Cookie.Id)) _selectedId = hit.Cookie.Id;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(hit.Cookie.Summary);
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##CookieDetails", new Vector2(0f, -1f));
        DrawDetails(catalogue.Find(_selectedId));
        ImGui.EndChild();
    }

    private void DrawDetails(Cookie? cookie)
    {
        if (cookie == null)
        {
            ImGui.TextDisabled("Pick a cookie.");
            return;
        }

        var manifest  = cookie.Manifest;
        var installed = _host.Lock.Find(cookie.Id);
        var jar       = _host.Catalogue().JarOf(cookie);

        ImGui.TextUnformatted($"{manifest.Name}  {manifest.Version}");
        ImGui.TextDisabled($"{cookie.JarName} · {string.Join(", ", manifest.Engines)}"
                         + (manifest.Tags.Count > 0 ? " · " + string.Join(", ", manifest.Tags) : ""));
        ImGui.Spacing();
        ImGui.TextWrapped(manifest.Summary);
        ImGui.Spacing();

        if (manifest.Provides.TypeNames.Any())
            ImGui.TextWrapped("Provides: " + string.Join(", ", manifest.Provides.TypeNames));
        if (manifest.Requires.Count > 0)
            ImGui.TextWrapped("Requires: " + string.Join(", ", manifest.Requires));

        ImGui.Separator();

        if (jar is { IsInstallable: false })
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.2f, 1f));
            ImGui.TextWrapped($"Jar '{jar.Name}' is not trusted, so its code cannot be installed. Trust it on the Jars tab.");
            ImGui.PopStyleColor();
        }

        bool canInstall = EditorState.CurrentProject != null && jar is not { IsInstallable: false } && !_busy;

        if (!canInstall) ImGui.BeginDisabled();
        if (ImGui.Button(installed == null ? "Install" : "Reinstall", new Vector2(110f, 0f)))
            Install(cookie, overwrite: installed != null);
        if (!canInstall) ImGui.EndDisabled();

        if (installed != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Uninstall", new Vector2(110f, 0f))) Uninstall(cookie.Id);
        }

        ImGui.SameLine();
        if (ImGui.Button("Reveal")) DesktopShell.RevealInFileManager(cookie.Directory);

        if (EditorState.CurrentProject == null) ImGui.TextDisabled("Open a project to install.");

        ImGui.Spacing();
        ImGui.TextDisabled("AGENT.md");
        ImGui.BeginChild("##Agent", new Vector2(0f, 0f), ImGuiChildFlags.Border);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(SafeAgent(cookie));
        ImGui.PopTextWrapPos();
        ImGui.EndChild();
    }

    private static string SafeAgent(Cookie cookie)
    {
        try { return cookie.AgentInstructions(); } catch (Exception ex) { return ex.Message; }
    }

    // -------------------------------------------------------------------------
    // Installed
    // -------------------------------------------------------------------------

    private void DrawInstalled()
    {
        var project = _host.Project;
        if (project == null)
        {
            ImGui.TextDisabled("Open a project first.");
            return;
        }

        var installed = _host.Lock;
        if (installed.Cookies.Count == 0)
        {
            ImGui.TextWrapped("Nothing installed. Anything you install is recorded in CookieJar.lock.json, which is meant "
                            + "to be committed with the game.");
            return;
        }

        foreach (var entry in installed.Cookies.ToList())
        {
            ImGui.PushID(entry.Id);
            ImGui.TextUnformatted($"{entry.Id}  {entry.Version}");
            ImGui.SameLine();
            ImGui.TextDisabled($"({entry.Jar}, {entry.Files.Count} files)");

            var drifted = entry.Files
                .Select(f => (f.Path, Hash: CookieLockFile.HashFile(Path.Combine(project.Root, f.Path.Replace('/', Path.DirectorySeparatorChar))), f.Sha256))
                .Where(f => f.Hash != f.Sha256)
                .ToList();

            if (drifted.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.1f, 1f));
                ImGui.TextWrapped($"{drifted.Count} file(s) changed since install; those are kept when you remove it.");
                ImGui.PopStyleColor();
            }

            if (ImGui.Button("Uninstall")) Uninstall(entry.Id);
            ImGui.SameLine();
            if (ImGui.Button("Reveal")) DesktopShell.RevealInFileManager(Path.Combine(project.Root, entry.Files.FirstOrDefault()?.Path ?? ""));

            ImGui.Separator();
            ImGui.PopID();
        }
    }

    // -------------------------------------------------------------------------
    // Jars
    // -------------------------------------------------------------------------

    private void DrawJars()
    {
        ImGui.TextWrapped("A jar is a folder or a git repository of cookies. Installing a cookie compiles and runs its "
                        + "code, so a cloned jar is only fetched once you trust it here. The assistant cannot do this.");
        ImGui.Separator();

        var catalogue = _host.Catalogue();

        foreach (var stored in _host.Settings.Jars.ToList())
        {
            ImGui.PushID(stored.Name);

            bool enabled = stored.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                stored.Enabled = enabled;
                _host.Settings.Save();
                _host.Catalogue(refresh: true);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(stored.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"({stored.Kind.ToString().ToLowerInvariant()}{(stored.Trusted ? "" : ", not trusted")})");

            if (stored.RemoteUrl != null) ImGui.TextDisabled(stored.RemoteUrl);

            if (stored.Kind == CookieJarKind.Git && (!stored.Trusted || !stored.Cloned))
            {
                if (!_busy && ImGui.Button("Trust and clone")) TrustAndClone(stored.Name);
            }
            else if (stored.Kind == CookieJarKind.Git)
            {
                if (!_busy && ImGui.Button("Refresh")) Refresh(stored.Name);
                ImGui.SameLine();
                ImGui.TextDisabled(stored.PinnedCommit?[..Math.Min(8, stored.PinnedCommit.Length)] ?? "");
            }

            ImGui.SameLine();
            if (ImGui.Button("Remove"))
            {
                _host.Settings.Remove(stored.Name);
                _host.Catalogue(refresh: true);
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        foreach (var jar in catalogue.Jars.Where(j => _host.Settings.Find(j.Name) == null))
        {
            ImGui.TextUnformatted(jar.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"({jar.Kind.ToString().ToLowerInvariant()}, found automatically) — {jar.Path}");
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-220f);
        ImGui.InputTextWithHint("##NewJar", "https://... or a folder path", ref _newJar, 512);
        ImGui.SameLine();
        if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(_newJar)) AddJar(_newJar);
        ImGui.SameLine();
        if (ImGui.Button("Add folder..."))
            FileDialog.PickFolder("Pick a cookie jar", null, path => AddJar(path));
    }

    // -------------------------------------------------------------------------
    // Bake
    // -------------------------------------------------------------------------

    private void DrawBake()
    {
        var project = _host.Project;
        if (project == null)
        {
            ImGui.TextDisabled("Open a project first.");
            return;
        }

        ImGui.SetNextItemWidth(220f); ImGui.InputTextWithHint("id", "double-jump", ref _bakeId, 64);
        ImGui.SetNextItemWidth(220f); ImGui.InputTextWithHint("name", "Double Jump", ref _bakeName, 64);
        ImGui.SetNextItemWidth(-1f);  ImGui.InputTextWithHint("summary", "One line: what is it?", ref _bakeSummary, 200);
        ImGui.SetNextItemWidth(-1f);  ImGui.InputTextWithHint("tags", "movement, platformer", ref _bakeTags, 128);

        ImGui.TextDisabled("AGENT.md — how to wire it up in the next game");
        ImGui.InputTextMultiline("##BakeAgent", ref _bakeAgent, 4000, new Vector2(-1f, 80f));

        ImGui.Separator();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##BakeFilter", "Filter files", ref _bakeFilter, 128);

        ImGui.BeginChild("##BakeFiles", new Vector2(0f, 160f), ImGuiChildFlags.Border);
        foreach (string relative in ProjectFiles(project))
        {
            if (_bakeFilter.Length > 0 && !relative.Contains(_bakeFilter, StringComparison.OrdinalIgnoreCase)) continue;

            bool picked = _bakeFiles.Contains(relative);
            if (ImGui.Checkbox(relative, ref picked))
            {
                if (picked) _bakeFiles.Add(relative);
                else        _bakeFiles.Remove(relative);
            }
        }
        ImGui.EndChild();

        ImGui.TextDisabled($"{_bakeFiles.Count} file(s) selected — into {_host.DefaultBakeJar}");

        bool ready = CookieJarLocator.SlugFor(_bakeId) == _bakeId
                  && _bakeFiles.Count > 0 && _bakeName.Length > 0 && _bakeSummary.Length > 0 && !_busy;

        if (!ready) ImGui.BeginDisabled();
        if (ImGui.Button("Dry run", new Vector2(100f, 0f))) Bake(project, dryRun: true);
        ImGui.SameLine();
        if (ImGui.Button("Bake", new Vector2(100f, 0f))) Bake(project, dryRun: false);
        if (!ready) ImGui.EndDisabled();
    }

    private static IEnumerable<string> ProjectFiles(CookieProjectContext project)
    {
        foreach (string folder in new[] { project.SourceDirectory, project.ScriptDirectory, project.SceneDirectory,
                                          project.AssetDirectory, project.ConfigDirectory })
        {
            string absolute = Path.Combine(project.Root, folder);
            if (!Directory.Exists(absolute)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories); }
            catch (Exception) { continue; }

            foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
                yield return Path.GetRelativePath(project.Root, file).Replace('\\', '/');
        }
    }

    // -------------------------------------------------------------------------
    // Actions
    // -------------------------------------------------------------------------

    private void Install(Cookie cookie, bool overwrite)
    {
        var project = _host.Project;
        if (project == null) return;

        Run($"Installing {cookie.Id}...", async () =>
        {
            var catalogue  = _host.Catalogue();
            var resolution = CookieDependencies.Resolve(catalogue, new[] { cookie.Id }, _host.Lock);

            if (resolution.Missing.Count > 0)
                throw new CookieException($"{cookie.Id} requires {string.Join(", ", resolution.Missing)}, which no jar has.");

            bool built = false;
            foreach (var each in resolution.Ordered)
            {
                if (!overwrite && _host.Lock.Find(each.Id) != null && each.Id != cookie.Id) continue;

                var plan = CookiePlanner.Plan(each, project, _host.Lock,
                                              new CookieInstallOptions(overwrite), catalogue.JarOf(each));

                if (!plan.IsApplicable) throw new CookieException(plan.FirstBlocker!.Detail);

                CookieInstaller.Apply(plan, project, _host.Lock, catalogue.JarOf(each));
                built |= plan.RequiresBuild;
            }

            await _host.AfterFilesChangedAsync(built).ConfigureAwait(false);
            return $"Installed {cookie.Id}. See its AGENT.md for what to do next.";
        });
    }

    private void Uninstall(string id)
    {
        var project = _host.Project;
        if (project == null) return;

        Run($"Removing {id}...", async () =>
        {
            var plan    = CookieUninstaller.Plan(project.Root, _host.Lock, id);
            var outcome = CookieUninstaller.Apply(project.Root, _host.Lock, plan);
            await _host.AfterFilesChangedAsync(plan.Cookie.Engines.Contains(CookieManifest.EngineCSharp)).ConfigureAwait(false);

            return outcome.Kept.Count > 0
                ? $"Removed {id}; kept {outcome.Kept.Count} file(s) you had edited."
                : $"Removed {id}.";
        });
    }

    private void AddJar(string urlOrPath)
    {
        try
        {
            var staged = _host.StageJar(urlOrPath);
            _newJar = "";
            SetStatus(staged.Kind == CookieJarKind.Git
                ? $"Added '{staged.Name}'. Press Trust and clone to fetch it."
                : $"Added '{staged.Name}'.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void TrustAndClone(string name)
        => Run($"Cloning {name}...", async () =>
        {
            await _host.TrustAndCloneAsync(name).ConfigureAwait(false);
            return $"Cloned '{name}'.";
        });

    private void Refresh(string name)
        => Run($"Refreshing {name}...", async () =>
        {
            var refresh = await _host.RefreshJarAsync(name).ConfigureAwait(false);
            return refresh.AffectsInstalled.Count > 0
                ? $"{name}: {refresh.Status}; this project uses {string.Join(", ", refresh.AffectsInstalled)}."
                : $"{name}: {refresh.Status}.";
        });

    private void Bake(CookieProjectContext project, bool dryRun)
    {
        try
        {
            var request = new BakeRequest(
                Project:           project,
                Id:                _bakeId,
                Name:              _bakeName,
                Summary:           _bakeSummary,
                AgentInstructions: _bakeAgent,
                DestinationJar:    _host.DefaultBakeJar,
                Files:             _bakeFiles.ToList(),
                Tags:              _bakeTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var plan = CookieBaker.Plan(request);

            if (dryRun)
            {
                SetStatus($"Would write {plan.Files.Count} file(s) to {plan.Directory}"
                        + (plan.IsApplicable ? "." : " — blocked: " + plan.FirstBlocker!.Detail));
                return;
            }

            var outcome = CookieBaker.Apply(plan);
            _host.Catalogue(refresh: true);

            int errors = outcome.Validation.Count(p => p.Severity == CookieSeverity.Error);
            SetStatus(errors == 0
                ? $"Baked '{_bakeId}' into {outcome.Directory}."
                : $"Baked '{_bakeId}' with {errors} problem(s); see the console.");

            foreach (var problem in outcome.Validation)
                ConsoleLog.Add("[cookiejar] " + problem, problem.Severity == CookieSeverity.Error ? LogLevel.Error : LogLevel.Warning);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    /// <summary>
    /// Runs work off the main thread and reports one line. The build inside an install marshals
    /// back through the dispatcher itself, so nothing here may block the frame.
    /// </summary>
    private void Run(string startedMessage, Func<Task<string>> work)
    {
        _busy = true;
        SetStatus(startedMessage);

        _ = Task.Run(async () =>
        {
            try
            {
                string message = await work().ConfigureAwait(false);
                SetStatus(message);
                ConsoleLog.Add("[cookiejar] " + message, LogLevel.Info);
            }
            catch (Exception ex)
            {
                string message = ex is CookieException ? ex.Message : ex.GetBaseException().Message;
                SetStatus(message);
                ConsoleLog.Add("[cookiejar] " + message, LogLevel.Error);
            }
            finally
            {
                _busy = false;
                _host.Catalogue(refresh: true);
            }
        });
    }

    private void SetStatus(string text)
    {
        _status      = text;
        _statusUntil = DateTime.UtcNow.AddSeconds(12);
    }

    private void DrawStatus()
    {
        if (DateTime.UtcNow > _statusUntil || _status.Length == 0) return;

        ImGui.Separator();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(_status);
        ImGui.PopTextWrapPos();
    }
}
