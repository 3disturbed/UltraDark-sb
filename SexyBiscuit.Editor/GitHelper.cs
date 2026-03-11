using System.ComponentModel;
using System.Diagnostics;

namespace SexyBiscuit.Editor;

public static class GitHelper
{
    private static (string stdout, string stderr, int exitCode) RunGit(string workingDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return ("", "Failed to start git process", -1);

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return ("", "Git process timed out", -1);
            }

            return (stdout.Trim(), stderr.Trim(), process.ExitCode);
        }
        catch (Win32Exception)
        {
            return ("", "Git is not installed or not in PATH", -1);
        }
    }

    public static bool IsGitInstalled()
    {
        var (_, _, exitCode) = RunGit(".", "--version");
        return exitCode == 0;
    }

    public static bool IsGitRepo(string workingDir)
    {
        var (stdout, _, exitCode) = RunGit(workingDir, "rev-parse --is-inside-work-tree");
        return exitCode == 0 && stdout.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static string? InitRepo(string workingDir)
    {
        var (_, stderr, exitCode) = RunGit(workingDir, "init");
        return exitCode == 0 ? null : stderr;
    }

    public static List<GitFileStatus> GetStatus(string workingDir)
    {
        var result = new List<GitFileStatus>();
        var (stdout, _, exitCode) = RunGit(workingDir, "status --porcelain");

        if (exitCode != 0 || string.IsNullOrEmpty(stdout))
            return result;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3)
                continue;

            string statusCode = line[..2].TrimEnd();
            string filePath = line[3..];

            result.Add(new GitFileStatus
            {
                StatusCode = statusCode,
                FilePath = filePath
            });
        }

        return result;
    }

    public static string GetCurrentBranch(string workingDir)
    {
        var (stdout, _, _) = RunGit(workingDir, "branch --show-current");
        return stdout;
    }

    public static List<string> GetBranches(string workingDir)
    {
        var result = new List<string>();
        var (stdout, _, exitCode) = RunGit(workingDir, "branch --list");

        if (exitCode != 0 || string.IsNullOrEmpty(stdout))
            return result;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string branch = line.TrimStart('*').Trim();
            if (!string.IsNullOrEmpty(branch))
                result.Add(branch);
        }

        return result;
    }

    public static List<GitLogEntry> GetLog(string workingDir, int maxCount = 50)
    {
        var result = new List<GitLogEntry>();
        var (stdout, _, exitCode) = RunGit(workingDir, $"log --pretty=format:\"%H|%s|%an|%ar\" -n {maxCount}");

        if (exitCode != 0 || string.IsNullOrEmpty(stdout))
            return result;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 4);
            if (parts.Length < 4)
                continue;

            result.Add(new GitLogEntry
            {
                Hash = parts[0],
                Message = parts[1],
                Author = parts[2],
                RelativeTime = parts[3]
            });
        }

        return result;
    }

    public static string GetDiff(string workingDir, string? filePath = null)
    {
        string arguments = filePath != null ? $"diff -- \"{filePath}\"" : "diff";
        var (stdout, _, _) = RunGit(workingDir, arguments);
        return stdout;
    }

    public static string GetStagedDiff(string workingDir)
    {
        var (stdout, _, _) = RunGit(workingDir, "diff --cached");
        return stdout;
    }

    public static string? Stage(string workingDir, string filePath)
    {
        var (_, stderr, exitCode) = RunGit(workingDir, $"add \"{filePath}\"");
        return exitCode == 0 ? null : stderr;
    }

    public static string? StageAll(string workingDir)
    {
        var (_, stderr, exitCode) = RunGit(workingDir, "add -A");
        return exitCode == 0 ? null : stderr;
    }

    public static string? Unstage(string workingDir, string filePath)
    {
        var (_, stderr, exitCode) = RunGit(workingDir, $"reset HEAD \"{filePath}\"");
        return exitCode == 0 ? null : stderr;
    }

    public static string? Commit(string workingDir, string message)
    {
        var (_, stderr, exitCode) = RunGit(workingDir, $"commit -m \"{message}\"");
        return exitCode == 0 ? null : stderr;
    }

    public static string? Checkout(string workingDir, string branchName)
    {
        var (_, stderr, exitCode) = RunGit(workingDir, $"checkout \"{branchName}\"");
        return exitCode == 0 ? null : stderr;
    }

    public static string? CreateBranch(string workingDir, string branchName)
    {
        var (_, stderr, exitCode) = RunGit(workingDir, $"checkout -b \"{branchName}\"");
        return exitCode == 0 ? null : stderr;
    }
}

public class GitFileStatus
{
    public string StatusCode { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool IsStaged => StatusCode.Length >= 1 && StatusCode[0] != ' ' && StatusCode[0] != '?';
}

public class GitLogEntry
{
    public string Hash { get; set; } = "";
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public string RelativeTime { get; set; } = "";
}
