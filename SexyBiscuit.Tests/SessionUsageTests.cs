using SexyBiscuit.Engine.Mcp.ClaudeCode;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>The session meter: per-turn deltas, result sizes by tool, and the jsonl line.</summary>
public class SessionUsageTests
{
    private static StreamFrame F(string line)
    {
        Assert.True(StreamJsonParser.TryParse(line, out var frame, out var error), error);
        return frame!;
    }

    private static Transcript TranscriptWithCalls(params (string tool, int chars)[] calls)
    {
        var t = new Transcript();
        int n = 0;
        foreach (var (tool, chars) in calls)
        {
            n++;
            t.Apply(F($$$"""{"type":"assistant","message":{"id":"m{{{n}}}","role":"assistant","content":[{"type":"tool_use","id":"t{{{n}}}","name":"mcp__sexybiscuit__{{{tool}}}","input":{}}]}}"""));
            t.Apply(F($$$"""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t{{{n}}}","content":"{{{new string('x', chars)}}}","is_error":false}]}}"""));
        }
        return t;
    }

    private static ResultFrame Result(int apiCalls, long input, long output, long cacheRead, long cacheCreate)
        => new("success", false, 0.5, apiCalls, 1200, null, null, input, output, cacheRead, cacheCreate, 0);

    [Fact] // Why: the meter exists to say which tools fill the context; grouping by tool and keeping the frame's numbers is its whole job.
    public void RecordGroupsResultSizesByToolAndKeepsTheTurnsNumbers()
    {
        var transcript = TranscriptWithCalls(("get_scene_summary", 5000), ("spawn_primitive", 100), ("spawn_primitive", 120));
        var usage = new SessionUsage();

        var turn = usage.Record(Result(apiCalls: 4, input: 1000, output: 300, cacheRead: 30000, cacheCreate: 2000), 0.05,
                                transcript.Entries.OfType<ToolCallEntry>());

        Assert.Equal(1, turn.Turn);
        Assert.Equal(33000, turn.ContextTokens);
        Assert.Equal(8250, turn.ContextPerCall);
        Assert.Equal(30000.0 / 33000.0, turn.CacheShare, 3);
        Assert.Equal(5220, turn.ResultChars);
        Assert.Equal(3, turn.ToolCalls);

        Assert.Equal("get_scene_summary", turn.Tools[0].Tool);   // largest first
        Assert.Equal(5000, turn.Tools[0].ResultChars);
        Assert.Equal("spawn_primitive", turn.Tools[1].Tool);
        Assert.Equal(2, turn.Tools[1].Calls);
        Assert.Equal(220, turn.Tools[1].ResultChars);

        Assert.Equal(1, usage.TurnCount);
        Assert.Equal(8250, usage.AverageContextPerCall);
        Assert.Equal(0.05, usage.CostUsd, 6);
    }

    [Fact] // Why: the display copy is capped at 64K, but the model read all of it; the meter must see the real size.
    public void Transcript_KeepsTheUncappedResultLengthOnAToolCall()
    {
        var transcript = TranscriptWithCalls(("get_scene_json", 100_000));
        var call = Assert.IsType<ToolCallEntry>(transcript.Entries.Single());

        Assert.Equal(100_000, call.ResultChars);
        Assert.True(call.ResultFull.Length < 100_000, "the display copy is truncated");
        Assert.Equal(0, call.ResultImageBytes);
    }

    [Fact] // Why: usage.jsonl is read back by tools and people; a line must round-trip without losing a field.
    public void ATurnRoundTripsThroughItsJsonLine()
    {
        var transcript = TranscriptWithCalls(("capture_viewport", 40), ("get_actor", 800));
        var usage = new SessionUsage();
        var turn = usage.Record(Result(2, 500, 120, 9000, 0), 0.0125, transcript.Entries.OfType<ToolCallEntry>());

        string line = turn.ToJsonLine();
        Assert.DoesNotContain("\n", line);

        var back = TurnUsage.FromJsonLine(line);
        Assert.NotNull(back);
        Assert.Equal(turn.Turn, back!.Turn);
        Assert.Equal(turn.ApiCalls, back.ApiCalls);
        Assert.Equal(turn.InputTokens, back.InputTokens);
        Assert.Equal(turn.CacheReadTokens, back.CacheReadTokens);
        Assert.Equal(turn.OutputTokens, back.OutputTokens);
        Assert.Equal(turn.CostUsd, back.CostUsd, 6);
        Assert.Equal(turn.Tools.Select(t => (t.Tool, t.Calls, t.ResultChars)), back.Tools.Select(t => (t.Tool, t.Calls, t.ResultChars)));
        Assert.Null(TurnUsage.FromJsonLine("not json"));
    }

    [Fact] // Why: the summary is what get_session_usage returns; it must rank tools by what they cost over the whole session.
    public void SummaryRanksToolsByResultSizeAcrossTurns()
    {
        var usage = new SessionUsage();
        usage.Record(Result(1, 100, 50, 1000, 0), 0.01, TranscriptWithCalls(("get_actor", 700), ("read_console", 300)).Entries.OfType<ToolCallEntry>());
        usage.Record(Result(2, 100, 50, 3000, 0), 0.02, TranscriptWithCalls(("read_console", 900)).Entries.OfType<ToolCallEntry>());

        var summary = usage.Summary(topTools: 1);

        Assert.Equal(2, summary["turns"]!.GetValue<int>());
        Assert.Equal(3, summary["apiCalls"]!.GetValue<int>());
        Assert.Equal(1900, summary["resultChars"]!.GetValue<long>());
        var top = summary["topTools"]!.AsArray();
        Assert.Single(top);
        Assert.Equal("read_console", top[0]!["tool"]!.GetValue<string>());
        Assert.Equal(1200, top[0]!["chars"]!.GetValue<long>());
        Assert.Equal(900, summary["lastTurn"]!["resultChars"]!.GetValue<long>());
        Assert.Contains("2 turns", usage.SummaryLine());
    }
}
