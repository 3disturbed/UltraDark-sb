using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Mcp.ClaudeCode;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// The tools through which a Claude session talks to the person at the editor: <c>say</c>,
/// <c>ask_user</c> and <c>wait_for_user</c>. They run off the main thread and block on the
/// <see cref="InteractionBoard"/>, which the Assistant panel answers.
/// </summary>
public sealed class UserInteraction
{
    private readonly InteractionBoard  _board;
    private readonly AssistantSettings _settings;

    public UserInteraction(InteractionBoard board, AssistantSettings settings)
    {
        _board    = board;
        _settings = settings;
    }

    private static bool IsEmbedded(McpCallContext context) => context.ClientName == "embedded";

    [McpTool("say",
        "Show a message to the user in the editor's Assistant panel. For sessions driving the editor from a terminal, " +
        "this is how the user reads you; the embedded assistant's own replies already appear there and need not call it.",
        MainThread = false, Label = "Say: {message}")]
    public McpToolResult Say(
        [McpParam("The message, plain text or light markdown")] string message,
        [McpParam("info, success, warning or error")] string? level = null,
        McpCallContext context = null!)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new McpToolException("message is empty.");

        _board.Say(message, level, IsEmbedded(context) ? "embedded" : "external");
        return McpToolResult.Json(new JsonObject { ["ok"] = true });
    }

    [McpTool("ask_user",
        "Ask the user a question in the editor and wait for the answer. choices become buttons; the user can also type " +
        "one unless allow_free_text is false. Blocks until answered, or returns a timeout or cancelled status. Use it " +
        "for decisions that are expensive to change later, not for routine ones.",
        MainThread = false, Label = "Ask: {question}")]
    public async Task<McpToolResult> AskUser(
        [McpParam("The question, plain text")] string question,
        [McpParam("Choices offered as buttons")] string[]? choices = null,
        [McpParam("Also accept a typed answer")] bool allowFreeText = true,
        [McpParam("Seconds to wait before giving up")] int timeoutSeconds = 900,
        McpCallContext context = null!,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new McpToolException("question is empty.");
        if (choices is { Length: 0 }) choices = null;
        if (choices == null && !allowFreeText) throw new McpToolException("With no choices, allow_free_text must be true or the user cannot answer.");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 3600));
        var answer  = await _board.AskAsync(question, choices, allowFreeText, timeout, cancellation, IsEmbedded(context) ? "embedded" : "external");

        if (answer == null)
        {
            return McpToolResult.Json(new JsonObject
            {
                ["status"]  = "timeout",
                ["message"] = $"No answer within {timeout.TotalSeconds:F0} s. Decide sensibly yourself and say what you chose, or ask again later.",
            });
        }

        return McpToolResult.Json(new JsonObject
        {
            ["answer"]       = answer.Text,
            ["choice_index"] = answer.ChoiceIndex,
            ["free_text"]    = answer.FreeText,
        });
    }

    [McpTool("wait_for_user",
        "For sessions driving the editor from a terminal: block until the user types in the editor's Assistant panel, " +
        "then return what they typed. Call it at the end of every turn and act on the result. Also returns a timeout, " +
        "cancelled or editor_closing status, or not_needed for the embedded assistant.",
        MainThread = false, Label = "Wait for the user")]
    public async Task<McpToolResult> WaitForUser(
        [McpParam("Seconds to wait")] int timeoutSeconds = 0,
        McpCallContext context = null!,
        CancellationToken cancellation = default)
    {
        if (IsEmbedded(context))
        {
            return McpToolResult.Json(new JsonObject
            {
                ["status"]  = "not_needed",
                ["message"] = "You are the embedded assistant: the user's replies arrive as ordinary user messages. End your turn instead of waiting.",
            });
        }

        if (_board.EmbeddedSessionActive)
        {
            return McpToolResult.Json(new JsonObject
            {
                ["status"]  = "embedded_session_active",
                ["message"] = "The editor's own assistant session owns the panel right now. Ask the user to stop it (Assistant panel > Stop session) if they want to drive from the terminal.",
            });
        }

        int seconds = timeoutSeconds <= 0 ? _settings.WaitForUserDefaultTimeoutSeconds : timeoutSeconds;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600));

        var (status, text) = await _board.WaitForPromptAsync(timeout, cancellation, "external");

        return status switch
        {
            WaitStatus.Prompt => McpToolResult.Json(new JsonObject
            {
                ["status"]       = "prompt",
                ["text"]         = text,
                ["pending_more"] = _board.PendingPromptCount > 0,
            }),
            WaitStatus.Timeout => McpToolResult.Json(new JsonObject
            {
                ["status"]  = "timeout",
                ["message"] = $"The user typed nothing for {timeout.TotalSeconds:F0} s. Call wait_for_user again, or end the session if you are done.",
            }),
            WaitStatus.EditorClosing => McpToolResult.Json(new JsonObject
            {
                ["status"]  = "editor_closing",
                ["message"] = "The editor is closing. Stop calling tools.",
            }),
            _ => McpToolResult.Json(new JsonObject
            {
                ["status"]  = "cancelled",
                ["message"] = "The user pressed Stop. Summarise where you are and call wait_for_user again.",
            }),
        };
    }
}
