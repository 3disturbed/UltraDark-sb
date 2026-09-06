namespace SexyBiscuit.Engine.CookieJar;

/// <summary>Where a jar came from, which is what decides whether its code may be installed.</summary>
public enum CookieJarKind
{
    /// <summary>The engine repository's own <c>CookieJar/</c>. Reviewed with the engine, so trusted.</summary>
    Builtin,

    /// <summary>A folder the user added in the editor. Trusted by the act of adding it.</summary>
    Folder,

    /// <summary>A cloned git repository. Untrusted until the user approves it in the editor.</summary>
    Git,
}

/// <summary>
/// One source of cookies. A jar is addressed by <see cref="Name"/> everywhere it is recorded,
/// never by path: the lock file is committed and a path is true on one machine only.
/// </summary>
public sealed record CookieJarSource(
    string        Name,
    CookieJarKind Kind,
    string        Path,
    string?       RemoteUrl      = null,
    string?       Branch         = null,
    bool          Enabled        = true,
    bool          Trusted        = false,
    DateTime?     TrustedUtc     = null,
    string?       PinnedCommit   = null,
    DateTime?     LastRefreshUtc = null)
{
    /// <summary>The name the engine repository's own jar always has.</summary>
    public const string BuiltinName = "builtin";

    /// <summary>
    /// Whether cookies from this jar may be installed. Builtin and folder jars are trusted by
    /// where they come from; a git jar has to be approved in the editor, and no tool can do it.
    /// </summary>
    public bool IsInstallable => Enabled && (Kind != CookieJarKind.Git || Trusted);

    /// <summary>A builtin jar at <paramref name="path"/>.</summary>
    public static CookieJarSource Builtin(string path)
        => new(BuiltinName, CookieJarKind.Builtin, path, Trusted: true);

    /// <summary>A local folder jar, trusted because the user chose it.</summary>
    public static CookieJarSource Folder(string name, string path)
        => new(name, CookieJarKind.Folder, path, Trusted: true);
}
