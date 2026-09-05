using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Engine.Code;

/// <summary>Everything a restarted editor needs to pick up where it left off.</summary>
public sealed class RelaunchState
{
    public int      Version            { get; set; } = 1;
    public string   Reason             { get; set; } = "";
    public DateTime WrittenAtUtc       { get; set; } = DateTime.UtcNow;
    public int      EditorPid          { get; set; }
    public string   WorkingDirectory   { get; set; } = "";
    public string?  ProjectFile        { get; set; }
    public string?  ScenePath          { get; set; }
    public bool     SceneWasAutosaved  { get; set; }
    public string?  SelectedActorName  { get; set; }
    public string?  SelectedLayer      { get; set; }
    public string?  AssistantSessionId { get; set; }
    public int      McpPort            { get; set; }
    public string?  BuildId            { get; set; }
    public string?  BuildSummary       { get; set; }
    public string?  EditorMvid         { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new();
}

/// <summary>Reads and writes the resume file. It is read once and deleted, so a crash loop cannot re-trigger it.</summary>
public static class RelaunchStateFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SexyBiscuit");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "resume.json");
        }
    }

    public static void Save(RelaunchState state, string? path = null)
        => File.WriteAllText(path ?? DefaultPath, JsonSerializer.Serialize(state, Json));

    /// <summary>The saved state, or null. The file is deleted whether or not it parsed.</summary>
    public static RelaunchState? TryReadAndDelete(string? path = null, TimeSpan? maxAge = null)
    {
        string file = path ?? DefaultPath;
        if (!File.Exists(file)) return null;

        RelaunchState? state = null;
        try
        {
            state = JsonSerializer.Deserialize<RelaunchState>(File.ReadAllText(file), Json);
        }
        catch (Exception)
        {
            state = null;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }

        if (state != null && maxAge.HasValue && DateTime.UtcNow - state.WrittenAtUtc > maxAge.Value)
            return null;

        return state;
    }
}

/// <summary>What the relaunch script needs to know.</summary>
public sealed record RelaunchPlan(
    int    WaitForPid,
    string DotnetPath,
    string RepoRoot,
    string EditorCsproj,
    string Configuration,
    string EditorDll,
    string StagingDll,
    string WorkingDirectory,
    string ResumeFile,
    string LogFile);

/// <summary>
/// Renders the detached script that waits for the editor to exit, rebuilds it in place (the
/// staged build already proved the source compiles) and starts it again with <c>--resume</c>.
/// </summary>
public static class RelaunchScript
{
    public static string RenderSh(RelaunchPlan plan) => $"""
        #!/bin/sh
        # Written by the SexyBiscuit editor to restart itself after an engine rebuild.
        exec >{Sh(plan.LogFile)} 2>&1
        echo "waiting for editor pid {plan.WaitForPid} to exit"
        while kill -0 {plan.WaitForPid} 2>/dev/null; do sleep 0.2; done
        DLL={Sh(plan.EditorDll)}
        cd {Sh(plan.RepoRoot)} && {Sh(plan.DotnetPath)} build {Sh(plan.EditorCsproj)} -c {Sh(plan.Configuration)} -nologo -v:q -tl:off
        if [ $? -ne 0 ]; then
          echo "in-place build failed; starting the staged build instead"
          DLL={Sh(plan.StagingDll)}
        fi
        cd {Sh(plan.WorkingDirectory)} && exec {Sh(plan.DotnetPath)} "$DLL" --resume {Sh(plan.ResumeFile)}

        """;

    public static string RenderPowerShell(RelaunchPlan plan) => $$"""
        # Written by the SexyBiscuit editor to restart itself after an engine rebuild.
        $ErrorActionPreference = 'Continue'
        Start-Transcript -Path {{Ps(plan.LogFile)}} | Out-Null
        try { Wait-Process -Id {{plan.WaitForPid}} -ErrorAction SilentlyContinue } catch { }
        $dll = {{Ps(plan.EditorDll)}}
        Set-Location {{Ps(plan.RepoRoot)}}
        & {{Ps(plan.DotnetPath)}} build {{Ps(plan.EditorCsproj)}} -c {{Ps(plan.Configuration)}} -nologo -v:q -tl:off
        if ($LASTEXITCODE -ne 0) {
          Write-Output 'in-place build failed; starting the staged build instead'
          $dll = {{Ps(plan.StagingDll)}}
        }
        Set-Location {{Ps(plan.WorkingDirectory)}}
        Start-Process -FilePath {{Ps(plan.DotnetPath)}} -ArgumentList @($dll, '--resume', {{Ps(plan.ResumeFile)}}) -WorkingDirectory {{Ps(plan.WorkingDirectory)}}
        Stop-Transcript | Out-Null

        """;

    /// <summary>Single-quoted for /bin/sh: the only special character inside is the quote itself.</summary>
    public static string Sh(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Single-quoted for PowerShell.</summary>
    public static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
}
