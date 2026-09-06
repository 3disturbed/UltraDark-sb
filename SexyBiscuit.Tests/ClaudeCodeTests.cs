using System.Text.Json;
using SexyBiscuit.Engine.Mcp.ClaudeCode;
using Xunit;

namespace SexyBiscuit.Tests;

// -------------------------------------------------------------------------
// Stream-json frames
// -------------------------------------------------------------------------

public class StreamJsonTests
{
    // Captured from Claude Code 2.1.260 (trimmed).
    private const string InitLine = """{"type":"system","subtype":"init","cwd":"/tmp","session_id":"dec7a48c-5463-4988-a9ce-db9a50afa714","tools":["Task","Bash","Read","mcp__sexybiscuit__get_project_info"],"mcp_servers":[{"name":"sexybiscuit","status":"connected"}],"model":"claude-haiku-4-5-20251001","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.260","uuid":"a1"}""";

    [Fact]
    public void Parse_AnInitFrameExposesTheMcpServerStatus()
    {
        Assert.True(StreamJsonParser.TryParse(InitLine, out var frame, out _));

        var init = Assert.IsType<InitFrame>(frame);
        Assert.Equal("dec7a48c-5463-4988-a9ce-db9a50afa714", init.SessionId);
        Assert.Equal("claude-haiku-4-5-20251001", init.Model);
        Assert.Equal("2.1.260", init.ClaudeCodeVersion);
        Assert.Contains("mcp__sexybiscuit__get_project_info", init.Tools);
        Assert.Single(init.McpServers);
        Assert.Equal(("sexybiscuit", "connected"), (init.McpServers[0].Name, init.McpServers[0].Status));
        Assert.Equal(InitLine, init.Raw);
    }

    [Fact]
    public void Parse_AnAssistantFrameYieldsTextAndToolUseBlocks()
    {
        const string line = """{"type":"assistant","message":{"id":"msg_1","model":"claude-opus-5","role":"assistant","stop_reason":"tool_use","content":[{"type":"text","text":"Placing a cube."},{"type":"tool_use","id":"toolu_1","name":"mcp__sexybiscuit__spawn_primitive","input":{"shape":"cube","name":"Crate"}}]},"parent_tool_use_id":null,"session_id":"s","uuid":"u"}""";
        Assert.True(StreamJsonParser.TryParse(line, out var frame, out _));

        var assistant = Assert.IsType<AssistantFrame>(frame);
        Assert.Equal("msg_1", assistant.MessageId);
        Assert.Equal("Placing a cube.", assistant.Text);
        Assert.Null(assistant.Error);

        var use = Assert.IsType<ToolUseBlock>(assistant.Content[1]);
        Assert.Equal("toolu_1", use.Id);
        Assert.Equal("mcp__sexybiscuit__spawn_primitive", use.Name);
        // The input outlives the parser's JsonDocument.
        Assert.Equal("Crate", use.Input!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public void Parse_AnApiErrorAssistantFrameIsFlagged()
    {
        const string line = """{"type":"assistant","message":{"id":"x","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","content":[{"type":"text","text":"Not logged in · Please run /login"}]},"session_id":"s","uuid":"u","error":"authentication_failed","is_api_error_message":true}""";
        Assert.True(StreamJsonParser.TryParse(line, out var frame, out _));

        var assistant = Assert.IsType<AssistantFrame>(frame);
        Assert.Equal("authentication_failed", assistant.Error);
        Assert.True(assistant.IsApiErrorMessage);
        Assert.Contains("Not logged in", assistant.Text);
    }

    [Fact]
    public void Parse_AStreamEventCarriesTheTextDelta()
    {
        const string start = """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"session_id":"s","uuid":"u"}""";
        const string delta = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}},"session_id":"s","uuid":"u"}""";
        const string tool  = """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_9","name":"Read","input":{}}},"session_id":"s"}""";
        const string json  = """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"file_path\":"}},"session_id":"s"}""";

        Assert.True(StreamJsonParser.TryParse(start, out var f1, out _));
        var s = Assert.IsType<StreamEventFrame>(f1);
        Assert.Equal(("content_block_start", 0, "text"), (s.EventType, s.Index, s.BlockType));

        Assert.True(StreamJsonParser.TryParse(delta, out var f2, out _));
        var d = Assert.IsType<StreamEventFrame>(f2);
        Assert.Equal(("content_block_delta", "text_delta", "Hello"), (d.EventType, d.DeltaType, d.Text));

        Assert.True(StreamJsonParser.TryParse(tool, out var f3, out _));
        var t = Assert.IsType<StreamEventFrame>(f3);
        Assert.Equal(("tool_use", "toolu_9", "Read"), (t.BlockType, t.ToolUseId, t.ToolName));

        Assert.True(StreamJsonParser.TryParse(json, out var f4, out _));
        var j = Assert.IsType<StreamEventFrame>(f4);
        Assert.Equal("input_json_delta", j.DeltaType);
        Assert.Equal("{\"file_path\":", j.Text);
    }

    [Fact]
    public void Parse_AToolResultUserFrameLinksToItsToolUseId()
    {
        const string line = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"Spawned Crate (id 7)"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}],"is_error":false}]},"parent_tool_use_id":null,"session_id":"s","uuid":"u"}""";
        Assert.True(StreamJsonParser.TryParse(line, out var frame, out _));

        var user = Assert.IsType<UserFrame>(frame);
        Assert.False(user.IsReplay);
        var result = Assert.Single(user.ToolResults);
        Assert.Equal("toolu_1", result.ToolUseId);
        Assert.Equal("Spawned Crate (id 7)", result.Text);
        Assert.False(result.IsError);
        Assert.True(result.HasImage);
        Assert.Equal("image/png", result.ImageMediaType);
    }

    [Fact]
    public void Parse_AReplayedUserMessageKeepsItsText()
    {
        const string line = """{"type":"user","message":{"role":"user","content":"Build a room"},"session_id":"s","parent_tool_use_id":null,"uuid":"u","isReplay":true}""";
        Assert.True(StreamJsonParser.TryParse(line, out var frame, out _));

        var user = Assert.IsType<UserFrame>(frame);
        Assert.True(user.IsReplay);
        Assert.Equal("Build a room", user.Text);
        Assert.Empty(user.ToolResults);
    }

    [Fact]
    public void Parse_AResultFrameExposesCostTurnsAndSubtype()
    {
        const string line = """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"duration_api_ms":1000,"num_turns":3,"result":"Done.","session_id":"s","total_cost_usd":0.0421,"usage":{"input_tokens":12,"output_tokens":34,"cache_read_input_tokens":5,"cache_creation_input_tokens":6},"permission_denials":[{"tool_name":"Bash"}],"terminal_reason":"completed","uuid":"u"}""";
        Assert.True(StreamJsonParser.TryParse(line, out var frame, out _));

        var result = Assert.IsType<ResultFrame>(frame);
        Assert.Equal("success", result.Subtype);
        Assert.False(result.IsError);
        Assert.Equal(0.0421, result.TotalCostUsd, 6);
        Assert.Equal(3, result.NumTurns);
        Assert.Equal(1234, result.DurationMs);
        Assert.Equal((12L, 34L, 5L, 6L), (result.InputTokens, result.OutputTokens, result.CacheReadTokens, result.CacheCreationTokens));
        Assert.Equal(1, result.PermissionDenials);
    }

    [Fact]
    public void Parse_AControlRequestExposesPermissionFields()
    {
        const string line = """{"type":"control_request","request_id":"req_1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf build"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"allow","destination":"session"}],"title":"Run a command","description":"Deletes the build folder","decision_reason":"Bash is not pre-approved","tool_use_id":"toolu_2","default_to_no":true}}""";
        Assert.True(StreamJsonParser.TryParse(line, out var frame, out _));

        var request = Assert.IsType<ControlRequestFrame>(frame);
        Assert.True(request.IsPermissionRequest);
        Assert.Equal("req_1", request.RequestId);
        Assert.Equal("Bash", request.ToolName);
        Assert.Equal("rm -rf build", request.Input!.Value.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Array, request.PermissionSuggestions!.Value.ValueKind);
        Assert.Equal("Run a command", request.Title);
        Assert.True(request.DefaultToNo);
        Assert.False(request.RequiresUserInteraction);
    }

    [Fact]
    public void Parse_ControlCancelAndResponsesAreRecognised()
    {
        Assert.True(StreamJsonParser.TryParse("""{"type":"control_cancel_request","request_id":"req_1"}""", out var cancel, out _));
        Assert.Equal("req_1", Assert.IsType<ControlCancelFrame>(cancel).RequestId);

        Assert.True(StreamJsonParser.TryParse("""{"type":"control_response","response":{"subtype":"success","request_id":"r2","response":{}}}""", out var response, out _));
        var r = Assert.IsType<ControlResponseFrame>(response);
        Assert.Equal(("r2", "success"), (r.RequestId, r.Subtype));
    }

    [Fact]
    public void Parse_KeepAliveAndUnknownTypesDoNotThrow()
    {
        Assert.True(StreamJsonParser.TryParse("""{"type":"keep_alive"}""", out var keepAlive, out _));
        Assert.IsType<KeepAliveFrame>(keepAlive);

        Assert.True(StreamJsonParser.TryParse("""{"type":"something_new","payload":{"a":[1,2,3]}}""", out var unknown, out _));
        Assert.Equal("something_new", Assert.IsType<UnknownFrame>(unknown).FrameType);

        // A known type with a surprising shape is kept, not dropped.
        Assert.True(StreamJsonParser.TryParse("""{"type":"result","usage":"weird"}""", out var odd, out _));
        Assert.NotNull(odd);

        Assert.True(StreamJsonParser.TryParse("""{"type":"system","subtype":"status","status":"requesting","session_id":"s"}""", out var status, out _));
        Assert.Equal("requesting", Assert.IsType<StatusFrame>(status).Status);
    }

    [Fact]
    public void Parse_ABlankOrNonJsonLineIsReportedNotThrown()
    {
        Assert.False(StreamJsonParser.TryParse("", out var f1, out var e1));
        Assert.Null(f1);
        Assert.NotNull(e1);

        Assert.False(StreamJsonParser.TryParse("warning: something happened", out _, out var e2));
        Assert.NotNull(e2);

        Assert.False(StreamJsonParser.TryParse("[1,2,3]", out _, out var e3));
        Assert.Equal("not a JSON object", e3);
    }

    [Fact]
    public void Write_AUserMessageFrameHasRoleUser()
    {
        string line = StreamJsonWriter.UserMessage("hello\nworld", "sess");
        Assert.DoesNotContain('\n', line);

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("user", root.GetProperty("type").GetString());
        Assert.Equal("user", root.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("hello\nworld", root.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("sess", root.GetProperty("session_id").GetString());
    }

    [Fact]
    public void Write_AnAllowResponseEchoesTheRequestIdAndInput()
    {
        using var input = JsonDocument.Parse("""{"command":"ls"}""");
        string line = StreamJsonWriter.ControlResponseAllow("req_1", input.RootElement);

        using var doc = JsonDocument.Parse(line);
        var response = doc.RootElement.GetProperty("response");
        Assert.Equal("control_response", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("success", response.GetProperty("subtype").GetString());
        Assert.Equal("req_1", response.GetProperty("request_id").GetString());
        Assert.Equal("allow", response.GetProperty("response").GetProperty("behavior").GetString());
        Assert.Equal("ls", response.GetProperty("response").GetProperty("updatedInput").GetProperty("command").GetString());
    }

    [Fact]
    public void Write_ADenyResponseCarriesTheMessage()
    {
        using var doc = JsonDocument.Parse(StreamJsonWriter.ControlResponseDeny("req_2", "The user said no."));
        var inner = doc.RootElement.GetProperty("response").GetProperty("response");
        Assert.Equal("deny", inner.GetProperty("behavior").GetString());
        Assert.Equal("The user said no.", inner.GetProperty("message").GetString());
    }

    [Fact]
    public void Write_ControlRequestsCarryTheirSubtype()
    {
        using var interrupt = JsonDocument.Parse(StreamJsonWriter.Interrupt("i1"));
        Assert.Equal("control_request", interrupt.RootElement.GetProperty("type").GetString());
        Assert.Equal("interrupt", interrupt.RootElement.GetProperty("request").GetProperty("subtype").GetString());

        using var mode = JsonDocument.Parse(StreamJsonWriter.SetPermissionMode("m1", "acceptEdits"));
        Assert.Equal("acceptEdits", mode.RootElement.GetProperty("request").GetProperty("mode").GetString());

        using var model = JsonDocument.Parse(StreamJsonWriter.SetModel("m2", null));
        Assert.Equal(JsonValueKind.Null, model.RootElement.GetProperty("request").GetProperty("model").ValueKind);
    }
}

// -------------------------------------------------------------------------
// Argv, prompts, environment
// -------------------------------------------------------------------------

public class ClaudeArgvTests
{
    private static ClaudeLaunchOptions Options(Action<ClaudeLaunchOptions>? _ = null) => new()
    {
        ProjectName = "Dungeon",
        ProjectRoot = "/games/dungeon",
        McpUrl      = "http://127.0.0.1:7340/mcp/",
        SessionId   = "11111111-2222-3333-4444-555555555555",
    };

    private static string? ValueOf(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void Build_AutonomousModeBypassesPermissions()
    {
        var args = ClaudeArgvBuilder.Build(Options());

        Assert.Equal("bypassPermissions", ValueOf(args, "--permission-mode"));
        Assert.Contains("--allow-dangerously-skip-permissions", args);
        Assert.Equal("stdio", ValueOf(args, "--permission-prompt-tool"));
        Assert.Equal("stream-json", ValueOf(args, "--input-format"));
        Assert.Equal("stream-json", ValueOf(args, "--output-format"));
        Assert.Contains("--include-partial-messages", args);
        Assert.Contains("--replay-user-messages", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("mcp__sexybiscuit", ValueOf(args, "--allowedTools"));
        Assert.Equal("AskUserQuestion", ValueOf(args, "--disallowedTools"));
        Assert.Equal("11111111-2222-3333-4444-555555555555", ValueOf(args, "--session-id"));
        Assert.DoesNotContain("--resume", args);
    }

    [Theory]
    [InlineData(ClaudePermissionMode.Ask, "manual")]
    [InlineData(ClaudePermissionMode.AcceptEdits, "acceptEdits")]
    [InlineData(ClaudePermissionMode.Auto, "auto")]
    public void Build_OtherModesMapToTheCliNames(ClaudePermissionMode mode, string expected)
    {
        var args = ClaudeArgvBuilder.Build(new ClaudeLaunchOptions { PermissionMode = mode, SessionId = "s" });
        Assert.Equal(expected, ValueOf(args, "--permission-mode"));
        Assert.DoesNotContain("--allow-dangerously-skip-permissions", args);
    }

    [Fact]
    public void Build_ResumeUsesResumeFlagAndForksOnRequest()
    {
        var args = ClaudeArgvBuilder.Build(new ClaudeLaunchOptions { ResumeSessionId = "old", ForkSession = true });
        Assert.Equal("old", ValueOf(args, "--resume"));
        Assert.Contains("--fork-session", args);
        Assert.DoesNotContain("--session-id", args);
    }

    [Fact]
    public void Build_ResumeAndSessionIdAreExclusive()
        => Assert.Throws<ArgumentException>(() => ClaudeArgvBuilder.Build(new ClaudeLaunchOptions { SessionId = "a", ResumeSessionId = "b" }));

    [Fact]
    public void Build_InlineMcpConfigCarriesThePortTimeoutAndEmbeddedHeader()
    {
        var args = ClaudeArgvBuilder.Build(Options());
        using var doc = JsonDocument.Parse(ValueOf(args, "--mcp-config")!);

        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("sexybiscuit");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:7340/mcp/", server.GetProperty("url").GetString());
        Assert.Equal(3_600_000, server.GetProperty("timeout").GetInt32());
        Assert.Equal("embedded", server.GetProperty("headers").GetProperty("X-SexyBiscuit-Client").GetString());
    }

    [Fact]
    public void Build_OptionalFlagsOnlyWhenSet()
    {
        var bare = ClaudeArgvBuilder.Build(Options());
        foreach (var flag in new[] { "--model", "--effort", "--max-budget-usd", "--max-turns", "--add-dir", "--thinking-display", "--name" })
            Assert.DoesNotContain(flag, bare);

        var full = ClaudeArgvBuilder.Build(new ClaudeLaunchOptions
        {
            SessionId = "s", Model = "opus", Effort = "high", MaxBudgetUsd = 2.5, MaxTurns = 7,
            Thinking = ClaudeThinkingDisplay.Summarized, DisplayName = "SexyBiscuit: Dungeon",
            IncludeProjectMcpServers = true, ExtraArgs = new[] { "--debug" },
        });
        Assert.Equal("opus", ValueOf(full, "--model"));
        Assert.Equal("high", ValueOf(full, "--effort"));
        Assert.Equal("2.5", ValueOf(full, "--max-budget-usd"));
        Assert.Equal("7", ValueOf(full, "--max-turns"));
        Assert.Equal("summarized", ValueOf(full, "--thinking-display"));
        Assert.Equal("SexyBiscuit: Dungeon", ValueOf(full, "--name"));
        Assert.DoesNotContain("--strict-mcp-config", full);
        Assert.Equal("--debug", full[^1]);
    }

    [Fact]
    public void Build_AddDirIsVariadicBeforeTheNextFlag()
    {
        var args = ClaudeArgvBuilder.Build(new ClaudeLaunchOptions { SessionId = "s", AddDirectories = new[] { "/engine", "/shared" } });
        int i = args.IndexOf("--add-dir");
        Assert.Equal("/engine", args[i + 1]);
        Assert.Equal("/shared", args[i + 2]);
        Assert.StartsWith("--", args[i + 3]);
    }

    [Fact]
    public void RemoveFlag_DropsTheFlagAndItsValue()
    {
        var args = new List<string> { "--print", "--thinking-display", "summarized", "--verbose" };
        Assert.True(ClaudeArgvBuilder.RemoveFlag(args, "--thinking-display"));
        Assert.Equal(new[] { "--print", "--verbose" }, args);
        Assert.False(ClaudeArgvBuilder.RemoveFlag(args, "--thinking-display"));

        var boolean = new List<string> { "--verbose", "--fork-session", "--print" };
        Assert.True(ClaudeArgvBuilder.RemoveFlag(boolean, "--fork-session"));
        Assert.Equal(new[] { "--verbose", "--print" }, boolean);
    }

    [Fact]
    public void UnknownOptionIn_ParsesCommanderErrors()
    {
        Assert.Equal("--thinking-display", ClaudeArgvBuilder.UnknownOptionIn("error: unknown option '--thinking-display'\n"));
        Assert.Null(ClaudeArgvBuilder.UnknownOptionIn("Not logged in"));
    }

    [Fact]
    public void Embedded_NamesTheProjectAndForbidsSayAndWaitForUser()
    {
        string prompt = ClaudeSystemPrompt.Embedded(new ClaudeLaunchOptions { ProjectName = "Dungeon", ProjectRoot = "/games/dungeon", EngineRepoRoot = "/src/sexybiscuit" });

        Assert.Contains("\"Dungeon\"", prompt);
        Assert.Contains("/games/dungeon", prompt);
        Assert.Contains("/src/sexybiscuit", prompt);
        Assert.Contains("Do not call say or wait_for_user", prompt);
        Assert.Contains("reload_game_code", prompt);
        Assert.Contains("rebuild_engine_and_restart", prompt);
        Assert.Contains("ask_user", prompt);
    }

    [Fact]
    public void Embedded_TellsTheAssistantToCheckTheCookieJarFirst()
    {
        // The prompt is what makes the library get used; a tool nobody is told about is a tool
        // nobody calls.
        string prompt = ClaudeSystemPrompt.Embedded(new ClaudeLaunchOptions { ProjectName = "Dungeon", ProjectRoot = "/games/dungeon" });

        Assert.Contains("search_cookies", prompt);
        Assert.Contains("install_cookie", prompt);
        Assert.Contains("bake_cookie", prompt);
    }

    [Fact]
    public void ExternalInstructions_RequireWaitForUserAndTellTheEmbeddedSessionToIgnore()
    {
        string text = ClaudeSystemPrompt.ExternalInstructions("Scene guidance.");
        Assert.StartsWith("If your system prompt already identifies you as the embedded", text);
        Assert.Contains("wait_for_user", text);
        Assert.Contains("say", text);
        Assert.EndsWith("Scene guidance.", text);
    }

    [Fact]
    public void Scrub_RemovesNestingMarkersAndKeepsConfig()
    {
        var env = new Dictionary<string, string?>
        {
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_SESSION_ID"] = "abc",
            ["CLAUDE_CODE_MESSAGING_SOCKET"] = "/tmp/sock",
            ["CLAUDE_CODE_ENTRYPOINT"] = "sdk-cli",
            ["CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH"] = "1",
            ["CLAUDE_PID"] = "42",
            ["CLAUDE_CONFIG_DIR"] = "/home/me/.claude",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "tok",
            ["ANTHROPIC_API_KEY"] = "key",
            ["PATH"] = "/usr/bin",
        };

        var scrubbed = ClaudeEnvironment.Scrub(env);

        Assert.False(scrubbed.ContainsKey("CLAUDECODE"));
        Assert.False(scrubbed.ContainsKey("CLAUDE_CODE_SESSION_ID"));
        Assert.False(scrubbed.ContainsKey("CLAUDE_CODE_MESSAGING_SOCKET"));
        Assert.False(scrubbed.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
        Assert.False(scrubbed.ContainsKey("CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH"));
        Assert.False(scrubbed.ContainsKey("CLAUDE_PID"));
        Assert.Equal("/home/me/.claude", scrubbed["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("tok", scrubbed["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal("key", scrubbed["ANTHROPIC_API_KEY"]);
        Assert.Equal("/usr/bin", scrubbed["PATH"]);
    }

    [Fact]
    public void ExtendPath_AppendsMissingEntriesOnce()
    {
        string path = ClaudeEnvironment.ExtendPath("/usr/bin:/bin", new[] { "/opt/homebrew/bin", "/usr/bin", "/opt/homebrew/bin/", "" }, ':');
        Assert.Equal("/usr/bin:/bin:/opt/homebrew/bin", path);

        Assert.Equal("/a", ClaudeEnvironment.ExtendPath(null, new[] { "/a" }, ':'));
    }
}

// -------------------------------------------------------------------------
// Locator
// -------------------------------------------------------------------------

public class ClaudeLocatorTests
{
    private static LocatorEnvironment Unix(IEnumerable<string> files, IDictionary<string, string[]>? dirs = null, string? overridePath = null, string path = "/usr/local/bin:/opt/homebrew/bin")
    {
        var set = new HashSet<string>(files);
        return new LocatorEnvironment
        {
            Override        = overridePath,
            PathVariable    = path,
            HomeDirectory   = "/Users/me",
            IsMacOS         = true,
            FileExists      = set.Contains,
            ListDirectories = d => dirs != null && dirs.TryGetValue(d, out var list) ? list : Array.Empty<string>(),
        };
    }

    [Fact]
    public void Candidates_OverrideComesFirstThenPathThenKnownLocations()
    {
        var env = Unix(new[] { "/custom/claude", "/opt/homebrew/bin/claude", "/Users/me/.local/bin/claude" }, overridePath: "/custom/claude");
        var candidates = ClaudeCodeLocator.Candidates(env);

        Assert.Equal(new[] { "/custom/claude", "/opt/homebrew/bin/claude", "/Users/me/.local/bin/claude" }, candidates.Select(c => c.Path));
        Assert.Equal("settings", candidates[0].Source);
        Assert.Equal("PATH", candidates[1].Source);
        Assert.Equal("native installer", candidates[2].Source);
    }

    [Fact]
    public void Candidates_DesktopBundleHighestVersionLastAndDeduplicated()
    {
        const string bundles = "/Users/me/Library/Application Support/Claude/claude-code";
        var dirs  = new Dictionary<string, string[]> { [bundles] = new[] { bundles + "/2.1.258", bundles + "/2.1.260", bundles + "/notes" } };
        var files = new[]
        {
            "/opt/homebrew/bin/claude",
            bundles + "/2.1.258/claude.app/Contents/MacOS/claude",
            bundles + "/2.1.260/claude.app/Contents/MacOS/claude",
        };

        var candidates = ClaudeCodeLocator.Candidates(Unix(files, dirs));

        Assert.Equal(3, candidates.Count);
        Assert.Equal("/opt/homebrew/bin/claude", candidates[0].Path);
        Assert.EndsWith("2.1.260/claude.app/Contents/MacOS/claude", candidates[1].Path);
        Assert.EndsWith("2.1.258/claude.app/Contents/MacOS/claude", candidates[2].Path);
        Assert.All(candidates.Skip(1), c => Assert.Equal("Claude desktop app", c.Source));
    }

    [Fact]
    public void Candidates_WindowsPrefersExeOverCmdOnPath()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\tools\claude.cmd", @"C:\tools\claude.exe", @"C:\Users\me\AppData\Roaming\npm\claude.cmd" };
        var env = new LocatorEnvironment
        {
            PathVariable  = @"C:\tools",
            HomeDirectory = @"C:\Users\me",
            IsWindows     = true,
            AppData       = @"C:\Users\me\AppData\Roaming",
            LocalAppData  = @"C:\Users\me\AppData\Local",
            FileExists    = files.Contains,
        };

        var candidates = ClaudeCodeLocator.Candidates(env).Select(c => c.Path).ToList();
        Assert.Equal(@"C:\tools\claude.exe", candidates[0]);
        Assert.Equal(@"C:\tools\claude.cmd", candidates[1]);
        Assert.Contains(@"C:\Users\me\AppData\Roaming\npm\claude.cmd", candidates);
    }

    [Fact]
    public void ParseVersion_ReadsTheCliOutput()
    {
        Assert.Equal(new Version(2, 1, 260), ClaudeCodeLocator.ParseVersion("2.1.260 (Claude Code)\n"));
        Assert.Null(ClaudeCodeLocator.ParseVersion("claude native binary not installed"));
        Assert.Null(ClaudeCodeLocator.ParseVersion(null));
    }
}

// -------------------------------------------------------------------------
// .mcp.json writer
// -------------------------------------------------------------------------

public class McpConfigWriterTests
{
    [Fact]
    public void Merge_CreatesADocumentWhenNoneExists()
    {
        string? merged = McpConfigWriter.Merge(null, "http://127.0.0.1:7331/mcp/");
        Assert.NotNull(merged);

        using var doc = JsonDocument.Parse(merged!);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("sexybiscuit");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:7331/mcp/", server.GetProperty("url").GetString());
        Assert.Equal(3_600_000, server.GetProperty("timeout").GetInt32());
        Assert.False(server.TryGetProperty("headers", out _));
    }

    [Fact]
    public void Merge_PreservesOtherServersAndKeysAndReplacesAStaleUrl()
    {
        const string existing = """{"mcpServers":{"github":{"type":"stdio","command":"gh-mcp"},"sexybiscuit":{"type":"http","url":"http://127.0.0.1:7331/mcp/"}},"custom":true}""";
        string merged = McpConfigWriter.Merge(existing, "http://127.0.0.1:7332/mcp/", "secret")!;

        using var doc = JsonDocument.Parse(merged);
        Assert.True(doc.RootElement.GetProperty("custom").GetBoolean());
        Assert.Equal("gh-mcp", doc.RootElement.GetProperty("mcpServers").GetProperty("github").GetProperty("command").GetString());

        var ours = doc.RootElement.GetProperty("mcpServers").GetProperty("sexybiscuit");
        Assert.Equal("http://127.0.0.1:7332/mcp/", ours.GetProperty("url").GetString());
        Assert.Equal("Bearer secret", ours.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void Merge_RefusesInvalidJson()
    {
        Assert.Null(McpConfigWriter.Merge("{ not json", "http://x/"));
        Assert.Null(McpConfigWriter.Merge("[1,2]", "http://x/"));
    }

    [Fact]
    public void WriteProjectConfig_WritesThenReportsUnchanged()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sb-mcpjson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("written", McpConfigWriter.WriteProjectConfig(dir, "http://127.0.0.1:7331/mcp/"));
            Assert.Equal("unchanged", McpConfigWriter.WriteProjectConfig(dir, "http://127.0.0.1:7331/mcp/"));
            Assert.Equal("written", McpConfigWriter.WriteProjectConfig(dir, "http://127.0.0.1:7333/mcp/"));

            File.WriteAllText(Path.Combine(dir, ".mcp.json"), "nonsense");
            Assert.StartsWith("skipped", McpConfigWriter.WriteProjectConfig(dir, "http://127.0.0.1:7331/mcp/"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

// -------------------------------------------------------------------------
// Transcript
// -------------------------------------------------------------------------

public class TranscriptTests
{
    private static StreamFrame F(string line)
    {
        Assert.True(StreamJsonParser.TryParse(line, out var frame, out var error), error);
        return frame!;
    }

    private static void StreamText(Transcript t, string messageId, int index, params string[] deltas)
    {
        t.Apply(F("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"" + messageId + "\"}}}"));
        t.Apply(F("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}}"));
        foreach (var d in deltas)
            t.Apply(F("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"text_delta\",\"text\":\"" + d + "\"}}}"));
    }

    [Fact]
    public void Apply_StreamDeltasCoalesceIntoOneAssistantEntry()
    {
        var t = new Transcript();
        StreamText(t, "m1", 0, "Hel", "lo ", "there");

        var entry = Assert.IsType<AssistantTextEntry>(Assert.Single(t.Entries));
        Assert.Equal("Hello there", entry.Text);
        Assert.True(entry.Streaming);
        Assert.Equal("Writing...", t.BusyStatus);

        t.Apply(F("""{"type":"stream_event","event":{"type":"content_block_stop","index":0}}"""));
        Assert.False(entry.Streaming);
    }

    [Fact]
    public void Apply_TheFinalAssistantFrameReplacesStreamedText()
    {
        var t = new Transcript();
        StreamText(t, "m1", 0, "Hello th");
        t.Apply(F("""{"type":"assistant","message":{"id":"m1","role":"assistant","content":[{"type":"text","text":"Hello there, friend."}]}}"""));

        var entry = Assert.IsType<AssistantTextEntry>(Assert.Single(t.Entries));
        Assert.Equal("Hello there, friend.", entry.Text);
        Assert.False(entry.Streaming);
    }

    [Fact]
    public void Apply_AToolCallStreamsItsInputAndAResultClosesIt()
    {
        var t = new Transcript();
        t.Apply(F("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m2"}}}"""));
        t.Apply(F("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"mcp__sexybiscuit__spawn_primitive","input":{}}}}"""));
        t.Apply(F("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"shape\":\"cube\","}}}"""));
        t.Apply(F("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"name\":\"Crate\"}"}}}"""));
        t.Apply(F("""{"type":"stream_event","event":{"type":"content_block_stop","index":0}}"""));

        var call = Assert.IsType<ToolCallEntry>(Assert.Single(t.Entries));
        Assert.True(call.InputComplete);
        Assert.Equal("Spawn cube 'Crate'", call.Label);
        Assert.Contains("shape=cube", call.ArgsCompact);
        Assert.Equal(ToolCallStatus.Running, call.Status);
        Assert.Equal("Running Spawn cube 'Crate'...", t.BusyStatus);

        // The authoritative assistant frame does not duplicate the call.
        t.Apply(F("""{"type":"assistant","message":{"id":"m2","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"mcp__sexybiscuit__spawn_primitive","input":{"shape":"cube","name":"Crate"}}]}}"""));
        Assert.Single(t.Entries);

        t.Apply(F("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"Spawned 'Crate' (id 7).\nmore detail","is_error":false}]}}"""));
        Assert.Equal(ToolCallStatus.Succeeded, call.Status);
        Assert.Equal("Spawned 'Crate' (id 7).", call.ResultSummary);
        Assert.Contains("more detail", call.ResultFull);
        Assert.Equal("Thinking...", t.BusyStatus);
    }

    [Fact]
    public void Apply_AnImageResultIsSummarisedAndKept()
    {
        var t = new Transcript();
        t.Apply(F("""{"type":"assistant","message":{"id":"m3","role":"assistant","content":[{"type":"tool_use","id":"toolu_2","name":"mcp__sexybiscuit__capture_viewport","input":{}}]}}"""));
        string data = new string('A', 4096);
        t.Apply(F("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_2\",\"content\":[{\"type\":\"text\",\"text\":\"Viewport 1024x576\"},{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + data + "\"}}],\"is_error\":false}]}}"));

        var call = Assert.IsType<ToolCallEntry>(Assert.Single(t.Entries));
        Assert.True(call.ResultIsImage);
        Assert.StartsWith("image (image/png, 3 KB)", call.ResultSummary);
        Assert.Contains("Viewport 1024x576", call.ResultSummary);
        Assert.Equal(data, call.ImageBase64);
        Assert.Equal("Look at the viewport", call.Label);
    }

    [Fact]
    public void Apply_AReplayedUserMessageAcksTheSentEntry()
    {
        var t = new Transcript();
        var user = t.AddUser("Build a room", UserEntryState.Sent);
        t.Apply(F("""{"type":"user","message":{"role":"user","content":"Build a room"},"isReplay":true}"""));
        Assert.Equal(UserEntryState.Acked, user.State);
        Assert.Equal(0, t.UnreadCount);
    }

    [Fact]
    public void Apply_AResultEndsTheTurnAndCancelsRunningCalls()
    {
        var t = new Transcript();
        t.Apply(F("""{"type":"assistant","message":{"id":"m4","role":"assistant","content":[{"type":"tool_use","id":"toolu_3","name":"Bash","input":{"command":"sleep 100"}}]}}"""));
        t.Apply(F("""{"type":"result","subtype":"success","is_error":false,"duration_ms":10,"num_turns":1,"total_cost_usd":0.5}"""));

        var call = Assert.IsType<ToolCallEntry>(t.Entries[0]);
        Assert.Equal(ToolCallStatus.Cancelled, call.Status);
        Assert.Equal("Run: sleep 100", call.Label);

        var result = Assert.IsType<ResultEntry>(t.Entries[1]);
        Assert.Equal(0.5, result.TotalCostUsd);
        Assert.Null(t.BusyStatus);
    }

    [Fact]
    public void Apply_APermissionRequestBecomesAnEntryAndCancelWithdrawsIt()
    {
        var t = new Transcript();
        t.Apply(F("""{"type":"control_request","request_id":"req_1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf build"},"default_to_no":true}}"""));

        var permission = Assert.IsType<PermissionEntry>(Assert.Single(t.Entries));
        Assert.Equal("Bash", permission.ToolName);
        Assert.Equal("Run: rm -rf build", permission.Title);
        Assert.True(permission.DefaultToNo);
        Assert.Equal(PermissionDecision.Pending, permission.Decision);
        Assert.Equal("Waiting for permission", t.BusyStatus);

        t.Apply(F("""{"type":"control_cancel_request","request_id":"req_1"}"""));
        Assert.Equal(PermissionDecision.Withdrawn, permission.Decision);
    }

    [Fact]
    public void Apply_AnApiErrorBecomesASystemEntry()
    {
        var t = new Transcript();
        t.Apply(F("""{"type":"assistant","message":{"id":"x","role":"assistant","content":[{"type":"text","text":"Not logged in · Please run /login"}]},"error":"authentication_failed","is_api_error_message":true}"""));

        var entry = Assert.IsType<SystemEntry>(Assert.Single(t.Entries));
        Assert.Equal(SystemSeverity.Error, entry.Severity);
        Assert.Contains("Not logged in", t.LastApiError);
    }

    [Fact]
    public void Cap_DropsOldEntriesButKeepsRunningCalls()
    {
        var t = new Transcript { MaxEntries = 3 };
        t.Apply(F("""{"type":"assistant","message":{"id":"m","role":"assistant","content":[{"type":"tool_use","id":"toolu_r","name":"Read","input":{"file_path":"/a/b/c.cs"}}]}}"""));
        for (int i = 0; i < 5; i++) t.AddSystem($"note {i}");

        Assert.Equal(3, t.Entries.Count);
        Assert.Contains(t.Entries, e => e is ToolCallEntry { Status: ToolCallStatus.Running, Label: "Read b/c.cs" });
        Assert.Equal("note 4", Assert.IsType<SystemEntry>(t.Entries[^1]).Text);
        Assert.True(t.UnreadCount > 0);
        t.MarkRead();
        Assert.Equal(0, t.UnreadCount);
    }

    [Fact]
    public void MarkdownLite_SplitsParagraphsBulletsNumberedHeadingsAndCode()
    {
        const string text = "# Plan\nFirst line\nsecond line\n\n- one\n* two\n1. first\n2) second\n```csharp\nvar x = 1;\n```\nTail **bold** [doc](http://x)";
        var blocks = MarkdownLite.Split(text);

        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("Plan", blocks[0].Text);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[1].Kind);
        Assert.Equal("First line\nsecond line", blocks[1].Text);
        Assert.Equal(MarkdownBlockKind.Bullet, blocks[2].Kind);
        Assert.Equal("one", blocks[2].Text);
        Assert.Equal(MarkdownBlockKind.Bullet, blocks[3].Kind);
        Assert.Equal(MarkdownBlockKind.Numbered, blocks[4].Kind);
        Assert.Equal(1, blocks[4].Number);
        Assert.Equal(MarkdownBlockKind.Numbered, blocks[5].Kind);
        Assert.Equal(2, blocks[5].Number);
        Assert.Equal(MarkdownBlockKind.Code, blocks[6].Kind);
        Assert.Equal("csharp", blocks[6].Language);
        Assert.Equal("var x = 1;", blocks[6].Text);
        Assert.Equal("Tail bold doc (http://x)", blocks[7].Text);
    }

    [Fact]
    public void MarkdownLite_KeepsAnUnterminatedCodeFence()
    {
        var blocks = MarkdownLite.Split("```\nstill typing");
        var code = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Code, code.Kind);
        Assert.Equal("still typing", code.Text);
    }

    [Fact]
    public void ToolLabels_AreFriendlyForCommonTools()
    {
        using var read = JsonDocument.Parse("""{"file_path":"/Users/me/game/Source/Spinner.cs"}""");
        Assert.Equal("Read Source/Spinner.cs", ToolLabels.Friendly("Read", read.RootElement));

        using var set = JsonDocument.Parse("""{"actor":"Cube","componentType":"MeshRenderer","property":"Metallic","value":1}""");
        Assert.Equal("Set MeshRenderer.Metallic on 'Cube'", ToolLabels.Friendly("mcp__sexybiscuit__set_property", set.RootElement));

        using var save = JsonDocument.Parse("""{"path":"Scenes/Main.scene"}""");
        Assert.Equal("Save scene 'Scenes/Main.scene'", ToolLabels.Friendly("mcp__sexybiscuit__save_scene", save.RootElement));

        using var custom = JsonDocument.Parse("""{"name":"Bob"}""");
        Assert.Equal("Game hello 'Bob'", ToolLabels.Friendly("mcp__sexybiscuit__game_hello", custom.RootElement));
        Assert.Equal("Reload game code", ToolLabels.Friendly("mcp__sexybiscuit__reload_game_code", null));
        Assert.Equal("spawn_actor", ToolLabels.ShortName("mcp__sexybiscuit__spawn_actor"));
    }

    [Fact]
    public void ResultSummariser_SummarisesLargeTextAndBinary()
    {
        var big = ResultSummariser.Summarise("first line\n" + new string('x', 20_000), false, 0, null);
        Assert.StartsWith("first line ...", big.Summary);
        Assert.Contains("KB", big.Summary);

        var binary = ResultSummariser.Summarise(new string('A', 5000), false, 0, null);
        Assert.StartsWith("binary data", binary.Summary);

        var huge = ResultSummariser.Summarise(string.Join(" ", Enumerable.Repeat("word", ToolCallEntry.MaxResultChars / 4)), false, 0, null);
        Assert.Contains("more characters", huge.Full);
    }
}

// -------------------------------------------------------------------------
// Interaction board
// -------------------------------------------------------------------------

public class InteractionBoardTests
{
    [Fact]
    public async Task WaitForPrompt_ReturnsAQueuedPromptImmediately()
    {
        var board = new InteractionBoard();
        board.QueuePrompt("make it red");

        var (status, text) = await board.WaitForPromptAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(WaitStatus.Prompt, status);
        Assert.Equal("make it red", text);
        Assert.Equal(0, board.PendingPromptCount);
    }

    [Fact]
    public async Task WaitForPrompt_TimesOut()
    {
        var board = new InteractionBoard();
        var (status, text) = await board.WaitForPromptAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Equal(WaitStatus.Timeout, status);
        Assert.Null(text);
        Assert.False(board.IsWaitingForPrompt);
    }

    [Fact]
    public async Task QueuePrompt_HandsToABlockedWaiter()
    {
        var board = new InteractionBoard();
        var wait = board.WaitForPromptAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!board.IsWaitingForPrompt && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(board.IsWaitingForPrompt);
        Assert.Equal(InteractionDriver.External, board.Driver);

        board.QueuePrompt("add a door");
        var (status, text) = await wait;
        Assert.Equal(WaitStatus.Prompt, status);
        Assert.Equal("add a door", text);
        Assert.Equal(0, board.PendingPromptCount);
    }

    [Fact]
    public async Task WaitForPrompt_CancellationReportsCancelled()
    {
        var board = new InteractionBoard();
        using var cts = new CancellationTokenSource();
        var wait = board.WaitForPromptAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.CancelAfter(20);

        var (status, _) = await wait;
        Assert.Equal(WaitStatus.Cancelled, status);
    }

    [Fact]
    public async Task CancelAll_ReleasesWaitersWithEditorClosing()
    {
        var board = new InteractionBoard();
        var wait = board.WaitForPromptAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!board.IsWaitingForPrompt && DateTime.UtcNow < deadline) await Task.Delay(5);

        board.CancelAll(editorClosing: true);
        var (status, _) = await wait;
        Assert.Equal(WaitStatus.EditorClosing, status);
    }

    [Fact]
    public async Task Ask_BlocksUntilAnsweredAndAChoiceUsesTheChoiceText()
    {
        var board = new InteractionBoard();
        var ask = board.AskAsync("Which style?", new[] { "Dungeon", "Forest" }, allowFreeText: true, TimeSpan.FromSeconds(5), CancellationToken.None);

        PendingQuestion? question = null;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while ((question = board.PendingQuestion) == null && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.NotNull(question);
        Assert.Equal("Which style?", question!.Text);

        Assert.False(board.Answer(question.Id, "", 5));      // out of range
        Assert.True(board.Answer(question.Id, "", 1));
        Assert.False(board.Answer(question.Id, "again", null)); // already answered

        var answer = await ask;
        Assert.NotNull(answer);
        Assert.Equal("Forest", answer!.Text);
        Assert.Equal(1, answer.ChoiceIndex);
        Assert.False(answer.FreeText);
        Assert.Equal(QuestionState.Answered, question.State);
    }

    [Fact]
    public async Task Ask_TimesOutToNull()
    {
        var board = new InteractionBoard();
        var answer = await board.AskAsync("Anyone?", null, true, TimeSpan.FromMilliseconds(30), CancellationToken.None);
        Assert.Null(answer);
        Assert.Null(board.PendingQuestion);
    }

    [Fact]
    public void Driver_ArbitratesEmbeddedThenExternalThenNone()
    {
        var board = new InteractionBoard();
        Assert.Equal(InteractionDriver.None, board.Driver);

        board.LastExternalSeenUtc = DateTime.UtcNow;
        Assert.Equal(InteractionDriver.External, board.Driver);

        board.EmbeddedSessionActive = true;
        Assert.Equal(InteractionDriver.Embedded, board.Driver);

        board.EmbeddedSessionActive = false;
        board.LastExternalSeenUtc = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        Assert.Equal(InteractionDriver.None, board.Driver);
    }

    [Fact]
    public async Task Events_AreRaisedForSayPromptsAndQuestions()
    {
        var board = new InteractionBoard();
        board.Say("Hello from outside", "info", "claude-code");
        board.QueuePrompt("queued text");
        var ask = board.AskAsync("Q?", null, true, TimeSpan.FromMilliseconds(30), CancellationToken.None);
        await ask;

        var kinds = new List<BoardEventKind>();
        while (board.TryDequeueEvent(out var e)) kinds.Add(e!.Kind);

        Assert.Equal(BoardEventKind.Said, kinds[0]);
        Assert.Equal(BoardEventKind.PromptQueued, kinds[1]);
        Assert.Contains(BoardEventKind.QuestionAsked, kinds);
        Assert.Contains(BoardEventKind.QuestionResolved, kinds);
        Assert.True(board.WithdrawPrompt("queued text"));
        Assert.False(board.WithdrawPrompt("queued text"));
    }
}
