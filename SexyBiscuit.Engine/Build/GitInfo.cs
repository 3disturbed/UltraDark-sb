namespace SexyBiscuit.Engine.Build;

/// <summary>
/// Reads the current commit of the repository a folder is in, from <c>.git</c> directly.
/// </summary>
/// <remarks>
/// A build report and an upload both carry the commit they came from. Spawning <c>git</c> for
/// it would make the pipeline depend on git being installed and on the PATH of whatever
/// launched the editor; the two files involved are trivial to read.
/// </remarks>
public static class GitInfo
{
    /// <summary>The 40-character HEAD commit of the repository containing <paramref name="directory"/>, or null.</summary>
    public static string? TryReadHeadSha(string directory)
    {
        string? current = Path.GetFullPath(directory);

        while (current != null)
        {
            string dotGit = Path.Combine(current, ".git");

            if (File.Exists(dotGit))
            {
                // A worktree or a submodule: ".git" is a file pointing at the real folder.
                var pointer = File.ReadAllText(dotGit).Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                return ReadHead(Path.GetFullPath(Path.Combine(current, pointer.Substring(7).Trim())));
            }

            if (Directory.Exists(dotGit)) return ReadHead(dotGit);

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static string? ReadHead(string gitDir)
    {
        try
        {
            string headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile)) return null;

            string head = File.ReadAllText(headFile).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal)) return head;

            string reference = head.Substring(4).Trim();
            string refFile   = Path.Combine(gitDir, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile)) return File.ReadAllText(refFile).Trim();

            string packed = Path.Combine(gitDir, "packed-refs");
            if (!File.Exists(packed)) return null;

            foreach (var line in File.ReadLines(packed))
            {
                if (line.EndsWith(" " + reference, StringComparison.Ordinal))
                    return line.Split(' ')[0];
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }
}
