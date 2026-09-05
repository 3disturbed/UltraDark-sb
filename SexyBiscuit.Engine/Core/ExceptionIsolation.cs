namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Lets a host survive game code that throws. With no handler installed exceptions propagate
/// exactly as before, which is what a shipped game wants: fail loudly. The editor installs a
/// handler that logs the exception, disables a component that keeps throwing, and keeps the
/// frame loop alive.
/// </summary>
public static class ExceptionIsolation
{
    /// <summary>
    /// Called with the exception, the actor or component that threw, and the lifecycle phase.
    /// Return true to swallow the exception, false to let it propagate.
    /// </summary>
    public static Func<Exception, object, string, bool>? Handler { get; set; }

    /// <summary>Used as an exception filter: true only when a handler exists and claims the exception.</summary>
    public static bool TryHandle(Exception exception, object owner, string phase)
    {
        var handler = Handler;
        if (handler == null) return false;

        try
        {
            return handler(exception, owner, phase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
