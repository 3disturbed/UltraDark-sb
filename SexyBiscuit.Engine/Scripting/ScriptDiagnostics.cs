namespace SexyBiscuit.Engine.Scripting;

/// <summary>Severity of a <see cref="ScriptDiagnostic"/>.</summary>
public enum ScriptDiagnosticLevel
{
    Log,
    Warning,
    Error,
}

/// <summary>
/// One message from a project script or from the runtime that hosts it: a <c>log()</c> call, a
/// warning the bridge raises, or an error thrown by a hook.
/// </summary>
/// <param name="Level">How serious it is.</param>
/// <param name="ScriptPath">The project-relative script the message came from; empty when unknown.</param>
/// <param name="Hook">The lifecycle hook that was running, when there was one.</param>
/// <param name="Message">The text itself.</param>
public readonly record struct ScriptDiagnostic(
    ScriptDiagnosticLevel Level,
    string                ScriptPath,
    string?               Hook,
    string                Message)
{
    /// <summary>The prefix the editor's console recognises: <c>[Script]</c>, <c>[Script WARN]</c>, <c>[Script Error]</c>.</summary>
    public string Prefix => Level switch
    {
        ScriptDiagnosticLevel.Error   => "[Script Error]",
        ScriptDiagnosticLevel.Warning => "[Script WARN]",
        _                             => "[Script]",
    };

    /// <inheritdoc />
    public override string ToString()
    {
        string where = string.IsNullOrEmpty(ScriptPath)
            ? string.Empty
            : Hook == null ? $" {ScriptPath}:" : $" {ScriptPath} ({Hook}):";
        return $"{Prefix}{where} {Message}";
    }
}

/// <summary>
/// The one place script output goes.
/// </summary>
/// <remarks>
/// Script errors used to be written with <c>System.Diagnostics.Debug.WriteLine</c>, which the
/// compiler removes from a Release build. A shipped game could not report a failing script, and
/// neither could a test run in Release. Everything now passes through <see cref="Report(ScriptDiagnostic)"/>: the
/// editor subscribes and shows it in its console, a test subscribes with <see cref="Capture"/>,
/// and with no subscriber at all the message goes to the process console so it is never lost.
/// </remarks>
public static class ScriptDiagnostics
{
    // -------------------------------------------------------------------------
    // Subscription
    // -------------------------------------------------------------------------

    /// <summary>Raised for every diagnostic. When nobody listens, diagnostics go to the console.</summary>
    public static event Action<ScriptDiagnostic>? Reported;

    /// <summary>
    /// Collects every diagnostic into <paramref name="into"/> until the returned handle is disposed.
    /// Intended for tests that tick a scene and then look at what its scripts said.
    /// </summary>
    public static IDisposable Capture(ICollection<ScriptDiagnostic> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        Action<ScriptDiagnostic> handler = d => { lock (into) into.Add(d); };
        Reported += handler;
        return new Subscription(() => Reported -= handler);
    }

    // -------------------------------------------------------------------------
    // Reporting
    // -------------------------------------------------------------------------

    /// <summary>Publishes <paramref name="diagnostic"/> to subscribers, or to the console when there are none.</summary>
    public static void Report(ScriptDiagnostic diagnostic)
    {
        var handlers = Reported;
        if (handlers != null)
        {
            handlers(diagnostic);
            return;
        }

        if (diagnostic.Level == ScriptDiagnosticLevel.Error)
            Console.Error.WriteLine(diagnostic.ToString());
        else
            Console.WriteLine(diagnostic.ToString());
    }

    /// <summary>Convenience overload of <see cref="Report(ScriptDiagnostic)"/>.</summary>
    public static void Report(ScriptDiagnosticLevel level, string scriptPath, string? hook, string message)
        => Report(new ScriptDiagnostic(level, scriptPath ?? string.Empty, hook, message ?? string.Empty));

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        public Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
