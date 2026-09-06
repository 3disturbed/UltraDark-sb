using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Mcp.ClaudeCode;

/// <summary>What one tool cost in a turn: how often it was called and how much it returned.</summary>
/// <param name="Tool">The tool name without the MCP server prefix.</param>
/// <param name="Calls">Calls in the turn.</param>
/// <param name="ResultChars">Characters of result text, before any display truncation.</param>
/// <param name="ImageBytes">Decoded bytes of image results.</param>
public sealed record ToolUsage(string Tool, int Calls, long ResultChars, long ImageBytes);

/// <summary>
/// One completed turn's usage: the numbers the result frame reported plus the tool results the
/// transcript saw during the turn.
/// </summary>
public sealed record TurnUsage(
    int                     Turn,
    DateTime                CompletedUtc,
    int                     ApiCalls,
    long                    DurationMs,
    double                  CostUsd,
    long                    InputTokens,
    long                    OutputTokens,
    long                    CacheReadTokens,
    long                    CacheCreationTokens,
    IReadOnlyList<ToolUsage> Tools)
{
    /// <summary>Everything the model read this turn, summed over its API calls: fresh input plus cache reads and writes.</summary>
    public long ContextTokens => InputTokens + CacheReadTokens + CacheCreationTokens;

    /// <summary>Average context per API call — the number that says how big the conversation has grown.</summary>
    public long ContextPerCall => ApiCalls > 0 ? ContextTokens / ApiCalls : ContextTokens;

    /// <summary>Share of the context served from cache, 0 to 1.</summary>
    public double CacheShare => ContextTokens > 0 ? (double)CacheReadTokens / ContextTokens : 0;

    /// <summary>Characters of tool results the model read this turn.</summary>
    public long ResultChars => Tools.Sum(t => t.ResultChars);

    /// <summary>Tool calls this turn.</summary>
    public int ToolCalls => Tools.Sum(t => t.Calls);

    /// <summary>The turn as one JSON object, the shape <c>usage.jsonl</c> stores.</summary>
    public JsonObject ToJson()
    {
        var tools = new JsonArray();
        foreach (var tool in Tools)
        {
            var entry = new JsonObject { ["tool"] = tool.Tool, ["calls"] = tool.Calls, ["chars"] = tool.ResultChars };
            if (tool.ImageBytes > 0) entry["imageBytes"] = tool.ImageBytes;
            tools.Add(entry);
        }

        return new JsonObject
        {
            ["turn"]          = Turn,
            ["completedUtc"]  = CompletedUtc.ToString("O"),
            ["apiCalls"]      = ApiCalls,
            ["durationMs"]    = DurationMs,
            ["costUsd"]       = Math.Round(CostUsd, 6),
            ["input"]         = InputTokens,
            ["output"]        = OutputTokens,
            ["cacheRead"]     = CacheReadTokens,
            ["cacheCreation"] = CacheCreationTokens,
            ["tools"]         = tools,
        };
    }

    /// <summary>One line of <c>usage.jsonl</c>.</summary>
    public string ToJsonLine() => ToJson().ToJsonString();

    /// <summary>Reads a line written by <see cref="ToJsonLine"/>; null when it is not one.</summary>
    public static TurnUsage? FromJsonLine(string line)
    {
        JsonObject? node;
        try { node = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (node == null) return null;

        var tools = new List<ToolUsage>();
        if (node["tools"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
                tools.Add(new ToolUsage(Str(entry, "tool"), (int)Num(entry, "calls"), Num(entry, "chars"), Num(entry, "imageBytes")));
        }

        DateTime completed = DateTime.TryParse(Str(node, "completedUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
            ? when : DateTime.MinValue;

        return new TurnUsage((int)Num(node, "turn"), completed, (int)Num(node, "apiCalls"), Num(node, "durationMs"),
                             node["costUsd"]?.GetValue<double>() ?? 0, Num(node, "input"), Num(node, "output"),
                             Num(node, "cacheRead"), Num(node, "cacheCreation"), tools);
    }

    private static string Str(JsonObject node, string key) => node[key]?.GetValue<string>() ?? string.Empty;

    private static long Num(JsonObject node, string key)
        => node[key] is JsonValue value && long.TryParse(value.ToJsonString(), out var n) ? n : 0;
}

/// <summary>
/// The session meter: one <see cref="TurnUsage"/> per completed turn, and the totals an agent
/// or a person needs to see what a task cost — context per call, cache share, output, and the
/// tools that returned the most text.
/// </summary>
/// <remarks>
/// The host records a turn from the result frame (tokens, cost, API calls) and the transcript's
/// tool calls since the previous turn (result sizes). Readers may come from any thread; the
/// list is locked.
/// </remarks>
public sealed class SessionUsage
{
    private readonly object _lock = new();
    private readonly List<TurnUsage> _turns = new();

    /// <summary>Every recorded turn, oldest first.</summary>
    public IReadOnlyList<TurnUsage> Turns
    {
        get { lock (_lock) return _turns.ToList(); }
    }

    /// <summary>Completed turns.</summary>
    public int TurnCount { get { lock (_lock) return _turns.Count; } }

    /// <summary>API calls over every turn.</summary>
    public int ApiCalls { get { lock (_lock) return _turns.Sum(t => t.ApiCalls); } }

    /// <summary>Context tokens over every turn: input plus cache reads and writes.</summary>
    public long ContextTokens { get { lock (_lock) return _turns.Sum(t => t.ContextTokens); } }

    /// <summary>Output tokens over every turn.</summary>
    public long OutputTokens { get { lock (_lock) return _turns.Sum(t => t.OutputTokens); } }

    /// <summary>Cache-read tokens over every turn.</summary>
    public long CacheReadTokens { get { lock (_lock) return _turns.Sum(t => t.CacheReadTokens); } }

    /// <summary>Characters of tool results over every turn.</summary>
    public long ResultChars { get { lock (_lock) return _turns.Sum(t => t.ResultChars); } }

    /// <summary>Image bytes returned by tools over every turn.</summary>
    public long ImageBytes { get { lock (_lock) return _turns.Sum(t => t.Tools.Sum(x => x.ImageBytes)); } }

    /// <summary>Tool calls over every turn.</summary>
    public int ToolCalls { get { lock (_lock) return _turns.Sum(t => t.ToolCalls); } }

    /// <summary>Cost over every turn, as the deltas the host handed in.</summary>
    public double CostUsd { get { lock (_lock) return _turns.Sum(t => t.CostUsd); } }

    /// <summary>Average context per API call over the session.</summary>
    public long AverageContextPerCall
    {
        get
        {
            lock (_lock)
            {
                int calls = _turns.Sum(t => t.ApiCalls);
                return calls > 0 ? _turns.Sum(t => t.ContextTokens) / calls : 0;
            }
        }
    }

    /// <summary>Share of the session's context that came from cache, 0 to 1.</summary>
    public double CacheShare
    {
        get
        {
            lock (_lock)
            {
                long context = _turns.Sum(t => t.ContextTokens);
                return context > 0 ? (double)_turns.Sum(t => t.CacheReadTokens) / context : 0;
            }
        }
    }

    /// <summary>
    /// Records a completed turn: the frame's token counts and API calls, the cost delta the host
    /// computed, and the tool calls the transcript saw during the turn, grouped by tool.
    /// </summary>
    public TurnUsage Record(ResultFrame result, double costUsd, IEnumerable<ToolCallEntry> calls)
    {
        var tools = calls
            .GroupBy(c => ToolLabels.ShortName(c.Name))
            .Select(g => new ToolUsage(g.Key, g.Count(), g.Sum(c => c.ResultChars), g.Sum(c => (long)c.ResultImageBytes)))
            .OrderByDescending(t => t.ResultChars + t.ImageBytes)
            .ToList();

        TurnUsage turn;
        lock (_lock)
        {
            turn = new TurnUsage(_turns.Count + 1, DateTime.UtcNow, Math.Max(1, result.NumTurns), result.DurationMs, costUsd,
                                 result.InputTokens, result.OutputTokens, result.CacheReadTokens, result.CacheCreationTokens, tools);
            _turns.Add(turn);
        }
        return turn;
    }

    /// <summary>
    /// The session in about seventy tokens: turns, context per call, cache share, output, the
    /// size of the tool results, cost, the last turn, and the tools that returned the most.
    /// </summary>
    public JsonObject Summary(int topTools = 5)
    {
        List<TurnUsage> turns;
        lock (_lock) turns = _turns.ToList();

        var byTool = turns.SelectMany(t => t.Tools)
            .GroupBy(t => t.Tool)
            .Select(g => new ToolUsage(g.Key, g.Sum(t => t.Calls), g.Sum(t => t.ResultChars), g.Sum(t => t.ImageBytes)))
            .OrderByDescending(t => t.ResultChars + t.ImageBytes)
            .Take(Math.Max(0, topTools));

        var top = new JsonArray();
        foreach (var tool in byTool)
        {
            var entry = new JsonObject { ["tool"] = tool.Tool, ["calls"] = tool.Calls, ["chars"] = tool.ResultChars };
            if (tool.ImageBytes > 0) entry["imageKb"] = tool.ImageBytes / 1024;
            top.Add(entry);
        }

        var summary = new JsonObject
        {
            ["turns"]          = turns.Count,
            ["apiCalls"]       = turns.Sum(t => t.ApiCalls),
            ["contextPerCall"] = AverageContextPerCall,
            ["cacheShare"]     = Math.Round(CacheShare, 2),
            ["outputTokens"]   = turns.Sum(t => t.OutputTokens),
            ["resultChars"]    = turns.Sum(t => t.ResultChars),
            ["toolCalls"]      = turns.Sum(t => t.ToolCalls),
            ["costUsd"]        = Math.Round(turns.Sum(t => t.CostUsd), 4),
            ["topTools"]       = top,
        };

        if (turns.Count > 0)
        {
            var last = turns[^1];
            summary["lastTurn"] = new JsonObject
            {
                ["apiCalls"]       = last.ApiCalls,
                ["contextPerCall"] = last.ContextPerCall,
                ["outputTokens"]   = last.OutputTokens,
                ["resultChars"]    = last.ResultChars,
                ["toolCalls"]      = last.ToolCalls,
            };
        }

        return summary;
    }

    /// <summary>The session in one line, for a panel or a log.</summary>
    public string SummaryLine()
    {
        lock (_lock)
        {
            if (_turns.Count == 0) return "no completed turns";
            int  calls   = _turns.Sum(t => t.ApiCalls);
            long context = _turns.Sum(t => t.ContextTokens);
            long perCall = calls > 0 ? context / calls : 0;
            double share = context > 0 ? (double)_turns.Sum(t => t.CacheReadTokens) / context : 0;
            return $"{_turns.Count} turns, {calls} API calls, {perCall:N0} ctx/call, {share:P0} cached, " +
                   $"{_turns.Sum(t => t.OutputTokens):N0} out, {_turns.Sum(t => t.ResultChars) / 1024} KB of results over " +
                   $"{_turns.Sum(t => t.ToolCalls)} tool calls, ${_turns.Sum(t => t.CostUsd):F2}";
        }
    }
}
