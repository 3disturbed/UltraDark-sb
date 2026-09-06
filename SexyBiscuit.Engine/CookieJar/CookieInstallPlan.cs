namespace SexyBiscuit.Engine.CookieJar;

/// <summary>What installing will do with one file.</summary>
public enum PlannedFileAction
{
    /// <summary>Nothing is there; the file is written.</summary>
    Create,

    /// <summary>Something is there and differs. Blocked unless the caller asked to overwrite.</summary>
    Overwrite,

    /// <summary>Something is there and is byte-for-byte the same, so the file is left alone.</summary>
    SkipIdentical,

    /// <summary>The destination is not allowed, and nothing will be written.</summary>
    Blocked,
}

/// <summary>One file an install would write.</summary>
public sealed record PlannedFile(
    string            SourceAbsolute,
    string            DestinationRelative,
    long              Bytes,
    PlannedFileAction Action,
    string?           Reason = null);

/// <summary>Why an install cannot proceed, or a caution about one that can.</summary>
public enum CookieConflictKind
{
    /// <summary>A destination already exists with different content.</summary>
    FileExists,

    /// <summary>Another installed cookie already declares a type with this name.</summary>
    TypeNameCollision,

    /// <summary>A destination would escape the project or land somewhere off limits.</summary>
    PathEscape,

    /// <summary>This cookie, at this version, is already installed.</summary>
    AlreadyInstalled,

    /// <summary>An older version would replace a newer one.</summary>
    VersionDowngrade,

    /// <summary>The cookie declares an engine version this build is outside of.</summary>
    EngineVersion,

    /// <summary>A required cookie is in no enabled jar.</summary>
    MissingDependency,

    /// <summary>Requirements form a loop.</summary>
    CyclicDependency,

    /// <summary>The jar has not been trusted in the editor, so its code may not be installed.</summary>
    UntrustedJar,

    /// <summary>A file in the cookie matches no rule, so it would not be installed.</summary>
    UnmappedFile,
}

/// <summary>One reason an install is blocked, or one thing worth knowing about it.</summary>
public sealed record CookieConflict(CookieConflictKind Kind, string Subject, string Detail, bool Blocking = true)
{
    public override string ToString() => $"{Kind}: {Subject} — {Detail}";
}

/// <summary>How an install should behave.</summary>
public sealed record CookieInstallOptions(
    bool Overwrite           = false,
    bool IncludeDependencies = true,
    string? PreferredEngine  = null);

/// <summary>
/// Everything installing a cookie would do, worked out without touching the disk. Planning and
/// applying are separate so an install can be dry-run, reported, and tested without a project.
/// </summary>
public sealed record CookieInstallPlan(
    Cookie                              Cookie,
    IReadOnlyList<PlannedFile>          Files,
    IReadOnlyList<CookieConflict>       Conflicts,
    IReadOnlyDictionary<string, string> PathMap,
    IReadOnlyList<string>               Directories,
    bool                                RequiresBuild)
{
    /// <summary>True when nothing blocks the install.</summary>
    public bool IsApplicable => !Conflicts.Any(c => c.Blocking);

    /// <summary>The files that will actually be written.</summary>
    public IEnumerable<PlannedFile> Writable
        => Files.Where(f => f.Action is PlannedFileAction.Create or PlannedFileAction.Overwrite);

    /// <summary>The first blocking reason, for a one-line error.</summary>
    public CookieConflict? FirstBlocker => Conflicts.FirstOrDefault(c => c.Blocking);
}

/// <summary>What an install actually did.</summary>
public sealed record CookieInstallOutcome(
    Cookie                        Cookie,
    IReadOnlyList<PlannedFile>    Written,
    IReadOnlyList<string>         Unresolved,
    int                           Rewrites,
    InstalledCookie               Record);
