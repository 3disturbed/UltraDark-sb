using System.Text.Json;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>Everything a tool may want to know about the call it is serving.</summary>
public sealed class McpCallContext
{
    /// <summary>The JSON-RPC id, or null for a call made outside a request (tests, self-test).</summary>
    public JsonElement? RequestId { get; init; }

    /// <summary>The client name from <c>initialize</c> or the transport, when known.</summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// Fires when the client disconnects, cancels the request, presses Stop in the editor, or
    /// the server shuts down. Long-running tools must observe it.
    /// </summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>How to reach the game thread from a tool that runs off it.</summary>
    public IMcpDispatcher Dispatcher { get; init; } = InlineMcpDispatcher.Instance;

    /// <summary>The client's progress token, when it asked for progress notifications.</summary>
    public JsonElement? ProgressToken { get; init; }

    /// <summary>Set by the transport once the response has switched to a stream.</summary>
    public Action<double, double?, string?>? ProgressSink { get; set; }

    /// <summary>A context with no request behind it.</summary>
    public static McpCallContext None { get; } = new();

    /// <summary>Sends a progress notification when the client asked for them; otherwise no-op.</summary>
    public void ReportProgress(double progress, double? total = null, string? message = null)
    {
        if (ProgressToken is null) return;
        ProgressSink?.Invoke(progress, total, message);
    }
}
