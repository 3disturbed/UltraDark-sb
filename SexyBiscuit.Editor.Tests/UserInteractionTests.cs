using SexyBiscuit.Editor.Assistant;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Mcp.ClaudeCode;
using Xunit;

namespace SexyBiscuit.Editor.Tests;

/// <summary>
/// The tools a session talks to the person through: say, ask_user and wait_for_user, over the
/// board the Assistant panel answers.
/// </summary>
/// <remarks>
/// ask_user blocks a session until someone replies, so its refusals matter more than its happy
/// path: a question with no choices and no free text can never be answered, and an empty question
/// is a dialog with nothing in it.
/// </remarks>
public class UserInteractionTests
{
    private static (UserInteraction Tools, InteractionBoard Board) Interaction()
    {
        var board = new InteractionBoard();
        return (new UserInteraction(board, new AssistantSettings()), board);
    }

    private static McpCallContext External => new() { ClientName = "external" };

    [Fact]
    public void SayPutsTheMessageOnTheBoard()
    {
        var (tools, board) = Interaction();

        var result = tools.Say("the level is laid out", level: "success", context: External);

        Assert.False(result.IsError);
        Assert.True(board.TryDequeueEvent(out var said));
        Assert.Equal(BoardEventKind.Said, said!.Kind);
        Assert.Contains("laid out", said.Text);
        Assert.Equal("success", said.Level);
    }

    [Fact]
    public void AnEmptyMessageOrQuestionIsRefusedRatherThanShown()
    {
        var (tools, _) = Interaction();

        Assert.Throws<McpToolException>(() => tools.Say("   ", context: External));
        Assert.ThrowsAsync<McpToolException>(() => tools.AskUser("  ", context: External)).GetAwaiter().GetResult();
    }

    [Fact]
    public void AQuestionNobodyCouldAnswerIsRefused()
    {
        // No choices and no free text is a dialog with no way out, and the session would block
        // on it until the timeout.
        var (tools, _) = Interaction();

        Assert.ThrowsAsync<McpToolException>(
            () => tools.AskUser("Which?", choices: null, allowFreeText: false, context: External)).GetAwaiter().GetResult();
    }

    [Fact]
    public void AnAnsweredQuestionReturnsTheChoiceTheUserPicked()
    {
        var (tools, board) = Interaction();

        var asking = tools.AskUser("Which template?", choices: new[] { "2D Platformer", "Top-Down RPG" }, context: External);

        // The panel answers on its own thread; wait for the question to appear, then reply.
        PendingQuestion? question = null;
        for (int i = 0; i < 200 && question == null; i++)
        {
            question = board.PendingQuestion;
            if (question == null) Thread.Sleep(10);
        }
        Assert.NotNull(question);
        Assert.True(question!.TryAnswer("Top-Down RPG", choiceIndex: 1));

        var result = asking.GetAwaiter().GetResult();
        Assert.False(result.IsError);
        Assert.Contains("Top-Down RPG", result.FirstText);
    }
}
