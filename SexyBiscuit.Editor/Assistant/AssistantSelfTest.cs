using System.Text.Json.Nodes;
using SexyBiscuit.Editor.GameCode;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Mcp.ClaudeCode;
using SexyBiscuit.Engine.Mcp.Tools;
using SexyBiscuit.Engine.Scene;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// Headless checks: <c>--assistant-selftest</c> spawns a real Claude Code against a headless
/// MCP server and asserts the round trip; <c>--dump-mcp-tools</c> prints the tool catalogue.
/// Neither opens a window.
/// </summary>
public static class AssistantSelfTest
{
    private const string CannedPrompt =
        "This is an automated self-test. Do exactly this, then stop: 1) call the tool mcp__sexybiscuit__get_project_info; " +
        "2) call the tool mcp__sexybiscuit__say with message \"SELFTEST-OK\"; 3) reply with the single word DONE.";

    /// <summary>A stand-in for the editor's get_project_info, so the canned prompt works without an EditorApp.</summary>
    public sealed class SelfTestTools
    {
        public int Calls;

        [McpTool("get_project_info", "Self-test stand-in: reports that no project is open.")]
        public McpToolResult GetProjectInfo()
        {
            Calls++;
            return McpToolResult.Json(new JsonObject { ["open"] = false, ["selfTest"] = true, ["message"] = "Headless self-test; no project is open." });
        }
    }

    public static int Run(LaunchOptions options)
    {
        var settings = AssistantSettings.Load();
        if (options.McpPort is { } port) settings.McpPort = port;

        if (options.DumpMcpTools) return DumpTools(settings, options.Markdown);
        return SelfTest(settings, options);
    }

    // -------------------------------------------------------------------------
    // --dump-mcp-tools
    // -------------------------------------------------------------------------

    private static int DumpTools(AssistantSettings settings, bool markdown)
    {
        using var scene = new HeadlessSceneHost(SceneTemplates.CreateDefault3D("Catalogue"));
        var (registry, _, _) = BuildHeadlessServer(scene, settings, new InteractionBoard(), out _);

        if (markdown)
        {
            Console.WriteLine(registry.DescribeMarkdown());
        }
        else
        {
            Console.WriteLine(new JsonObject { ["tools"] = registry.DescribeForToolsList() }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        Console.Error.WriteLine($"{registry.Tools.Count} tools (engine + interaction; editor-only tools such as play and capture_viewport need the running editor).");
        return 0;
    }

    private static (McpToolRegistry Registry, McpResourceRegistry Resources, McpServer Server) BuildHeadlessServer(
        HeadlessSceneHost scene, AssistantSettings settings, InteractionBoard board, out SelfTestTools selfTestTools)
    {
        var dispatcher = InlineMcpDispatcher.Instance;
        var registry   = new McpToolRegistry(dispatcher);
        var resources  = new McpResourceRegistry(dispatcher);
        var undo       = new SceneUndoStack(scene);

        var engine = new McpRegistrationOptions { Source = "engine" };
        registry.RegisterInstance(new SceneTools(scene, undo), engine);
        registry.RegisterInstance(new ActorTools(scene), engine);
        registry.RegisterInstance(new ComponentTools(scene), engine);
        registry.RegisterInstance(new MaterialTools(scene), engine);
        registry.RegisterInstance(new UndoTools(undo), engine);
        registry.RegisterInstance(new UserInteraction(board, settings), new McpRegistrationOptions { Source = "editor" });
        selfTestTools = new SelfTestTools();
        registry.RegisterInstance(selfTestTools, new McpRegistrationOptions { Source = "editor" });
        resources.RegisterInstance(new SceneResources(scene, undo, registry));

        var server = new McpServer(registry, resources, new McpServerInfo("sexybiscuit", McpHost.EngineVersion, ClaudeSystemPrompt.ExternalInstructions(SceneResources.Instructions)));
        return (registry, resources, server);
    }

    // -------------------------------------------------------------------------
    // --assistant-selftest
    // -------------------------------------------------------------------------

    private static int SelfTest(AssistantSettings settings, LaunchOptions options)
    {
        Console.WriteLine("SexyBiscuit assistant self-test");
        Console.WriteLine("===============================");

        // 1. Find a working binary.
        var install = new ClaudeCodeInstall();
        install.DetectAsync(settings.ClaudePath).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("Claude Code candidates:");
        if (install.Verdicts.Count == 0) Console.WriteLine("  (none found)");
        foreach (var v in install.Verdicts)
            Console.WriteLine($"  [{(v.Works ? "ok" : "--")}] {v.Candidate.Path}  ({v.Candidate.Source}): {v.Detail}");

        if (install.Info == null)
        {
            Console.WriteLine();
            Console.WriteLine("FAIL: no working claude binary. " + ClaudeCodeLocator.InstallInstructions(OperatingSystem.IsWindows()));
            return options.DryRun ? 0 : 2;
        }

        Console.WriteLine($"Using: {install.Info.Path} ({install.Info.VersionText})");

        // 2. A headless MCP server with the engine tools and the interaction tools.
        var board = new InteractionBoard();
        using var scene = new HeadlessSceneHost(SceneTemplates.CreateDefault3D("SelfTest"));
        var (_, _, server) = BuildHeadlessServer(scene, settings, board, out var selfTestTools);

        var transport = new McpHttpTransport(server, new McpHttpTransportOptions { Port = settings.McpPort, PortSearchRange = 20 }, (m, e) => { if (e) Console.Error.WriteLine(m); });
        try
        {
            transport.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: could not start the MCP server: {ex.Message}");
            return 2;
        }

        string url = transport.BoundUrl!.ToString();
        Console.WriteLine($"MCP server: {url}");

        // 3. The command line, exactly as the editor would build it.
        string cwd = Path.Combine(Path.GetTempPath(), "sexybiscuit-selftest");
        Directory.CreateDirectory(cwd);

        var launch = new ClaudeLaunchOptions
        {
            ProjectName              = "SelfTest",
            ProjectRoot              = cwd,
            McpUrl                   = url,
            PermissionMode           = ClaudePermissionMode.Autonomous,
            Model                    = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model,
            MaxTurns                 = 4,
            SessionId                = Guid.NewGuid().ToString(),
            IncludeProjectMcpServers = false,
            DisplayName              = "SexyBiscuit self-test",
            ExtraArgs                = new[] { "--no-session-persistence" },
        };
        var argv = ClaudeArgvBuilder.Build(launch);

        Console.WriteLine();
        Console.WriteLine("Command line:");
        Console.WriteLine("  " + ClaudeArgvBuilder.Display(install.Info.Path, argv));

        if (options.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run: not spawning Claude Code. PASS (setup)");
            transport.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            return 0;
        }

        // 4. Run one canned conversation.
        var transcript = new Transcript();
        var session    = new ClaudeCodeSession(install.Info, argv, cwd, launch.SessionId!, transcript, (m, e) => Console.Error.WriteLine(m));

        bool   initOk = false, toolCalled = false, toolResultOk = false, saidOk = false;
        string? resultSubtype = null;
        bool   resultIsError = false, resultSeen = false;
        var    saidTexts = new List<string>();

        session.FrameReceived += frame =>
        {
            switch (frame)
            {
                case InitFrame init:
                    initOk = init.McpServers.Any(s => s.Name == "sexybiscuit" && s.Status == "connected");
                    Console.WriteLine($"  init: model {init.Model}, permission {init.PermissionMode}, mcp {string.Join(", ", init.McpServers.Select(s => s.Name + "=" + s.Status))}, apiKeySource {init.ApiKeySource}");
                    break;
                case AssistantFrame assistant:
                    foreach (var block in assistant.Content)
                    {
                        if (block is ToolUseBlock use)
                        {
                            Console.WriteLine($"  tool_use: {use.Name}");
                            if (use.Name == "mcp__sexybiscuit__get_project_info") toolCalled = true;
                        }
                        else if (block is TextBlock text && text.Text.Length > 0)
                        {
                            Console.WriteLine($"  assistant: {Trim(text.Text)}");
                        }
                    }
                    if (assistant.Error != null) Console.WriteLine($"  api error: {assistant.Error}: {Trim(assistant.Text)}");
                    break;
                case UserFrame user:
                    foreach (var r in user.ToolResults)
                    {
                        Console.WriteLine($"  tool_result: {(r.IsError ? "ERROR " : "")}{Trim(r.Text)}");
                        if (!r.IsError && r.Text.Contains("selfTest", StringComparison.OrdinalIgnoreCase)) toolResultOk = true;
                    }
                    break;
                case ResultFrame result:
                    resultSeen    = true;
                    resultSubtype = result.Subtype;
                    resultIsError = result.IsError;
                    Console.WriteLine($"  result: {result.Subtype}, error={result.IsError}, turns={result.NumTurns}, cost=${result.TotalCostUsd:F4}, {result.DurationMs} ms{(result.IsError ? ": " + Trim(result.ResultText ?? "") : "")}");
                    break;
            }
        };

        Console.WriteLine();
        Console.WriteLine("Frames:");
        session.Start();
        session.SendUserMessage(options.SelfTestPrompt ?? CannedPrompt);

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, options.SelfTestTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            session.Pump(maxFrames: 1000, budgetMs: 50);

            while (board.TryDequeueEvent(out var e) && e != null)
            {
                if (e.Kind == BoardEventKind.Said && e.Text != null)
                {
                    saidTexts.Add(e.Text);
                    Console.WriteLine($"  say: {Trim(e.Text)}");
                    if (e.Text.Contains("SELFTEST-OK", StringComparison.Ordinal)) saidOk = true;
                }
            }

            if (resultSeen || session.State == SessionState.Exited) break;
            Thread.Sleep(20);
        }

        bool timedOut = !resultSeen && session.State != SessionState.Exited;
        if (timedOut) Console.WriteLine($"  (timed out after {options.SelfTestTimeoutSeconds} s)");

        session.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        var stopDeadline = DateTime.UtcNow.AddSeconds(3);
        while (session.State != SessionState.Exited && DateTime.UtcNow < stopDeadline)
        {
            session.Pump(maxFrames: 1000, budgetMs: 50);
            Thread.Sleep(20);
        }

        transport.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        // 5. Verdict.
        Console.WriteLine();
        Console.WriteLine("Checks:");
        Check("Claude Code started and reported the sexybiscuit MCP server as connected", initOk);
        Check("get_project_info was called through MCP", toolCalled && selfTestTools.Calls > 0);
        Check("the tool result came back without error", toolResultOk);
        Check("say(\"SELFTEST-OK\") reached the interaction board", saidOk);
        Check("the turn ended with a successful result", resultSeen && !resultIsError && resultSubtype == "success");
        Check("the process exited cleanly", session.ExitCode is 0 or null && !timedOut);

        bool pass = initOk && toolCalled && toolResultOk && saidOk && resultSeen && !resultIsError && !timedOut;

        if (!pass)
        {
            Console.WriteLine();
            if (session.NotLoggedIn)
            {
                Console.WriteLine("Claude Code is not signed in for this user. Run the binary above once in a terminal and sign in:");
                Console.WriteLine($"  \"{install.Info.Path}\"    then type /login");
                Console.WriteLine("or set ANTHROPIC_API_KEY. The Claude desktop app's own sign-in is not shared with the standalone binary.");
            }
            if (session.ExitReason != null) Console.WriteLine("Exit: " + session.ExitReason);

            var stderr = session.StderrTail;
            if (stderr.Count > 0)
            {
                Console.WriteLine("stderr:");
                foreach (var line in stderr.TakeLast(20)) Console.WriteLine("  " + line);
            }
        }

        Console.WriteLine();
        Console.WriteLine(pass ? "PASS" : "FAIL");
        return pass ? 0 : 1;
    }

    private static void Check(string what, bool ok) => Console.WriteLine($"  [{(ok ? "ok" : "!!")}] {what}");

    private static string Trim(string text)
    {
        text = text.Replace("\n", " ").Trim();
        return text.Length <= 160 ? text : text[..159] + "...";
    }
}
