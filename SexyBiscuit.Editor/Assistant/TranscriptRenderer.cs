using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Mcp.ClaudeCode;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// Draws the transcript with the editor's single default font: role gutters, light markdown,
/// bordered code blocks, one-line tool rows that expand, question and permission cards, and
/// images decoded on demand. Sticks to the bottom until the user scrolls up.
/// </summary>
public sealed class TranscriptRenderer
{
    private const int VisibleEntries = 200;

    private static readonly Vector4 UserColour       = new(0.55f, 0.75f, 1.00f, 1f);
    private static readonly Vector4 ClaudeColour     = new(0.95f, 0.72f, 0.45f, 1f);
    private static readonly Vector4 DimColour        = new(0.55f, 0.55f, 0.58f, 1f);
    private static readonly Vector4 OkColour         = new(0.45f, 0.85f, 0.50f, 1f);
    private static readonly Vector4 WarnColour       = new(1.00f, 0.75f, 0.25f, 1f);
    private static readonly Vector4 ErrorColour      = new(1.00f, 0.42f, 0.42f, 1f);
    private static readonly Vector4 RunningColour    = new(0.60f, 0.80f, 1.00f, 1f);
    private static readonly Vector4 CodeBackground   = new(0.09f, 0.09f, 0.11f, 1f);
    private static readonly Vector4 CardBackground   = new(0.15f, 0.15f, 0.19f, 1f);
    private static readonly Vector4 PermissionBorder = new(0.85f, 0.60f, 0.20f, 1f);

    private readonly Dictionary<long, IntPtr>  _images      = new();
    private readonly Dictionary<long, Vector2> _imageSizes  = new();
    private readonly HashSet<long>             _imageFailed = new();
    private readonly HashSet<long>             _showImage   = new();
    private readonly Dictionary<long, string>  _answerDrafts = new();

    private bool _stickToBottom = true;
    private int  _lastVersion   = -1;
    private bool _showEarlier;
    private int  _pruneCountdown = 300;

    /// <summary>Scrolls to the newest entry on the next draw.</summary>
    public void JumpToLatest()
    {
        _stickToBottom = true;
        _lastVersion   = -1;
    }

    public void Draw(AssistantHost host, ImGuiRenderer imGui, float footerHeight)
    {
        var transcript = host.Transcript;
        bool changed = transcript.Version != _lastVersion;

        ImGui.BeginChild("##Transcript", new Vector2(0f, -footerHeight), ImGuiChildFlags.None, ImGuiWindowFlags.None);

        // Learn whether the user scrolled away, but not on a frame where the content moved under them.
        if (!changed)
        {
            float max = ImGui.GetScrollMaxY();
            _stickToBottom = max <= 0f || ImGui.GetScrollY() >= max - 8f;
        }

        ImGui.PushTextWrapPos(0f);

        var entries = transcript.Entries;
        int start = _showEarlier ? 0 : Math.Max(0, entries.Count - VisibleEntries);
        if (start > 0 && ImGui.SmallButton($"Show {start} earlier message(s)")) _showEarlier = true;

        if (entries.Count == 0) DrawEmptyState(host);

        for (int i = start; i < entries.Count; i++)
        {
            ImGui.PushID((int)entries[i].Id);
            DrawEntry(host, entries[i], imGui);
            ImGui.PopID();
            ImGui.Spacing();
        }

        ImGui.PopTextWrapPos();

        if (changed)
        {
            _lastVersion = transcript.Version;
            if (_stickToBottom) ImGui.SetScrollHereY(1f);
        }

        if (!_stickToBottom && entries.Count > 0)
        {
            // A floating button in the child's bottom-right corner.
            var windowPos  = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            var buttonSize = new Vector2(110f, 22f);
            ImGui.SetCursorScreenPos(new Vector2(windowPos.X + windowSize.X - buttonSize.X - 16f, windowPos.Y + windowSize.Y - buttonSize.Y - 8f));
            if (ImGui.Button("Jump to latest", buttonSize)) JumpToLatest();
        }

        ImGui.EndChild();

        if (--_pruneCountdown <= 0)
        {
            _pruneCountdown = 300;
            PruneImages(transcript, imGui);
        }
    }

    private static void DrawEmptyState(AssistantHost host)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, DimColour);
        if (host.Disabled)
            ImGui.TextWrapped("The embedded assistant is off. Connect a terminal Claude Code to this editor (Diagnostics tab) and its say/ask_user messages show up here.");
        else if (host.ProjectRoot == null)
            ImGui.TextWrapped("Open a project and Claude starts here. Tell it what to build; it works in the editor while you watch, and you can stop it at any time.");
        else
            ImGui.TextWrapped("Tell Claude what to build. It works in the editor while you watch: spawn things, tweak materials, write C#, run the game. Press Stop at any time; Undo reverts scene changes.");
        ImGui.PopStyleColor();
    }

    // -------------------------------------------------------------------------
    // Entries
    // -------------------------------------------------------------------------

    private void DrawEntry(AssistantHost host, TranscriptEntry entry, ImGuiRenderer imGui)
    {
        switch (entry)
        {
            case UserEntry user:               DrawUser(host, user); break;
            case AssistantTextEntry assistant: DrawAssistant(assistant); break;
            case ThinkingEntry thinking:       DrawThinking(thinking); break;
            case ToolCallEntry call:           DrawToolCall(call, imGui); break;
            case QuestionEntry question:       DrawQuestion(host, question); break;
            case PermissionEntry permission:   DrawPermission(host, permission); break;
            case SystemEntry system:           DrawSystem(system); break;
            case ResultEntry result:           DrawResult(result); break;
        }
    }

    private static void Gutter(string label, Vector4 colour, string? suffix = null)
    {
        ImGui.TextColored(colour, label);
        if (suffix != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(DimColour, suffix);
        }
    }

    private static void CopyButton(string text)
    {
        float width = ImGui.CalcTextSize("copy").X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - width);
        if (ImGui.SmallButton("copy")) DesktopShell.SetClipboardText(text);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this message");
    }

    private void DrawUser(AssistantHost host, UserEntry user)
    {
        string label  = user.IsSynthetic ? "Editor" : "You";
        string? state = user.State switch
        {
            UserEntryState.Queued => "(queued, not sent yet)",
            UserEntryState.Sent   => "(sending...)",
            _                     => null,
        };

        Gutter(label, user.IsSynthetic ? DimColour : UserColour, state);
        if (user.State == UserEntryState.Queued)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) host.WithdrawQueued(user);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Withdraw this message");
        }
        CopyButton(user.Text);

        if (user.State != UserEntryState.Acked || user.IsSynthetic) ImGui.PushStyleColor(ImGuiCol.Text, DimColour);
        ImGui.Indent(12f);
        ImGui.TextUnformatted(user.Text);
        ImGui.Unindent(12f);
        if (user.State != UserEntryState.Acked || user.IsSynthetic) ImGui.PopStyleColor();
    }

    private void DrawAssistant(AssistantTextEntry entry)
    {
        Gutter("Claude", ClaudeColour, entry.ViaSay ? "(said)" : entry.Streaming ? "..." : null);
        CopyButton(entry.Text);

        ImGui.Indent(12f);
        DrawMarkdown(entry.Text, entry.Id);
        ImGui.Unindent(12f);
    }

    private void DrawMarkdown(string text, long entryId)
    {
        int codeIndex = 0;
        foreach (var block in MarkdownLite.Split(text))
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Paragraph:
                    ImGui.TextUnformatted(block.Text);
                    break;

                case MarkdownBlockKind.Heading:
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
                    ImGui.TextUnformatted(block.Text.ToUpperInvariant());
                    ImGui.PopStyleColor();
                    break;

                case MarkdownBlockKind.Bullet:
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.TextUnformatted(block.Text);
                    break;

                case MarkdownBlockKind.Numbered:
                    ImGui.TextDisabled($"{block.Number}.");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(block.Text);
                    break;

                case MarkdownBlockKind.Code:
                    DrawCode(block.Text, $"##code{entryId}_{codeIndex++}", block.Language);
                    break;
            }
        }
    }

    private static void DrawCode(string code, string id, string? language)
    {
        int lines = Math.Max(1, code.Count(c => c == '\n') + 1);
        float height = Math.Min(lines, 14) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2f + 4f;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, CodeBackground);
        ImGui.BeginChild(id, new Vector2(0f, height), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.PopStyleColor();

        // Code must not wrap, or indentation lies.
        ImGui.PushTextWrapPos(float.MaxValue);
        ImGui.TextUnformatted(code);
        ImGui.PopTextWrapPos();

        ImGui.EndChild();

        if (language != null)
        {
            ImGui.TextDisabled(language);
            ImGui.SameLine();
        }
        if (ImGui.SmallButton("copy code" + id)) DesktopShell.SetClipboardText(code);
    }

    private static void DrawThinking(ThinkingEntry thinking)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, DimColour);
        if (ImGui.TreeNodeEx(thinking.Streaming ? "Thinking..." : "Thinking", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            ImGui.TextUnformatted(thinking.Text);
            ImGui.TreePop();
        }
        ImGui.PopStyleColor();
    }

    private void DrawToolCall(ToolCallEntry call, ImGuiRenderer imGui)
    {
        (string glyph, Vector4 colour) = call.Status switch
        {
            ToolCallStatus.Running   => ("[..]", RunningColour),
            ToolCallStatus.Succeeded => ("[ok]", OkColour),
            ToolCallStatus.Failed    => ("[!!]", ErrorColour),
            _                        => ("[--]", DimColour),
        };

        string duration = call.Status == ToolCallStatus.Running ? "" : $"  {call.Duration.TotalMilliseconds:F0} ms";
        string header   = $"{glyph} {call.Label}{duration}";

        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        bool open = ImGui.TreeNodeEx(header + "##call", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen);
        ImGui.PopStyleColor();

        if (call.ArgsCompact.Length > 0 && !open)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(call.ArgsCompact);
        }

        if (!open) return;

        ImGui.Indent(20f);
        ImGui.TextDisabled(call.Name);

        if (call.InputJson.Length > 0)
        {
            ImGui.TextDisabled("Arguments");
            string json = call.Input is { } input ? Engine.Mcp.McpJson.PrettyPrint(input) : call.InputJson.ToString();
            DrawCode(json, $"##args{call.Id}", null);
        }

        if (call.Status != ToolCallStatus.Running)
        {
            ImGui.TextDisabled(call.Status == ToolCallStatus.Failed ? "Error" : "Result");
            if (call.ResultFull.Length > 0) DrawCode(call.ResultFull, $"##result{call.Id}", null);
            else ImGui.TextDisabled(call.ResultSummary.Length > 0 ? call.ResultSummary : "(empty)");

            if (call.ResultIsImage && call.ImageBase64 != null)
            {
                bool showing = _showImage.Contains(call.Id);
                if (ImGui.SmallButton(showing ? "Hide image" : "Show image"))
                {
                    if (showing) _showImage.Remove(call.Id);
                    else _showImage.Add(call.Id);
                }

                if (_showImage.Contains(call.Id)) DrawImage(call, imGui);
            }
        }

        ImGui.Unindent(20f);
    }

    private void DrawImage(ToolCallEntry call, ImGuiRenderer imGui)
    {
        if (_imageFailed.Contains(call.Id))
        {
            ImGui.TextDisabled("(image could not be decoded)");
            return;
        }

        if (!_images.TryGetValue(call.Id, out var textureId))
        {
            try
            {
                var bytes = Convert.FromBase64String(call.ImageBase64!);
                using var stream = new MemoryStream(bytes);
                var texture = Texture2D.FromStream(EditorApp.Instance.GraphicsDevice, stream);
                textureId = imGui.BindTexture(texture);
                _images[call.Id]     = textureId;
                _imageSizes[call.Id] = new Vector2(texture.Width, texture.Height);
            }
            catch (Exception)
            {
                _imageFailed.Add(call.Id);
                return;
            }
        }

        var size  = _imageSizes[call.Id];
        float width = Math.Min(size.X, ImGui.GetContentRegionAvail().X - 8f);
        float scale = size.X > 0 ? width / size.X : 1f;
        ImGui.Image(textureId, new Vector2(width, size.Y * scale));
    }

    private void PruneImages(Transcript transcript, ImGuiRenderer imGui)
    {
        if (_images.Count == 0) return;
        var live = new HashSet<long>(transcript.Entries.Select(e => e.Id));
        foreach (var id in _images.Keys.Where(id => !live.Contains(id)).ToList())
        {
            imGui.UnbindTexture(_images[id]);
            _images.Remove(id);
            _imageSizes.Remove(id);
            _showImage.Remove(id);
        }
    }

    private void DrawQuestion(AssistantHost host, QuestionEntry entry)
    {
        var question = entry.Question;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBackground);
        ImGui.BeginChild($"##question{entry.Id}", new Vector2(0f, 0f), ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY, ImGuiWindowFlags.None);
        ImGui.PopStyleColor();

        Gutter("Claude asks", ClaudeColour, question.Client == "external" ? "(from the terminal session)" : null);
        ImGui.TextUnformatted(question.Text);
        ImGui.Spacing();

        switch (question.State)
        {
            case QuestionState.Pending:
            {
                for (int i = 0; i < question.Choices.Count; i++)
                {
                    if (i > 0) ImGui.SameLine();
                    if (ImGui.Button(question.Choices[i] + $"##choice{i}")) host.AnswerQuestion(question, question.Choices[i], i);
                }

                if (question.AllowFreeText)
                {
                    string draft = _answerDrafts.GetValueOrDefault(entry.Id, "");
                    ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 80f));
                    bool submit = ImGui.InputTextWithHint($"##answer{entry.Id}", "Type an answer...", ref draft, 2048, ImGuiInputTextFlags.EnterReturnsTrue);
                    _answerDrafts[entry.Id] = draft;
                    ImGui.SameLine();
                    if (ImGui.Button("Answer") || submit)
                    {
                        if (!string.IsNullOrWhiteSpace(draft))
                        {
                            host.AnswerQuestion(question, draft.Trim(), null);
                            _answerDrafts.Remove(entry.Id);
                        }
                    }
                }

                var remaining = question.Timeout - (DateTime.UtcNow - question.AskedUtc);
                ImGui.TextDisabled(remaining > TimeSpan.Zero ? $"Waiting for you · {remaining.TotalMinutes:F0} min left" : "Waiting for you");
                break;
            }
            case QuestionState.Answered:
                ImGui.TextColored(OkColour, $"You answered: {question.Answer?.Text}");
                break;
            case QuestionState.TimedOut:
                ImGui.TextColored(WarnColour, "No answer in time; Claude carried on.");
                break;
            default:
                ImGui.TextColored(DimColour, "Cancelled.");
                break;
        }

        ImGui.EndChild();
    }

    private void DrawPermission(AssistantHost host, PermissionEntry entry)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, PermissionBorder);
        ImGui.BeginChild($"##permission{entry.Id}", new Vector2(0f, 0f), ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY, ImGuiWindowFlags.None);
        ImGui.PopStyleColor(2);

        Gutter("Permission", WarnColour, entry.ToolName);
        ImGui.TextUnformatted(entry.Title);
        if (entry.Description.Length > 0) ImGui.TextDisabled(entry.Description);

        if (entry.InputJson.Length > 0 && ImGui.TreeNodeEx("Details##perm", ImGuiTreeNodeFlags.None))
        {
            DrawCode(entry.InputJson, $"##perminput{entry.Id}", null);
            ImGui.TreePop();
        }

        var session = host.Session;
        if (entry.Decision == PermissionDecision.Pending && session != null)
        {
            if (ImGui.Button("Allow")) session.RespondToPermission(entry, PermissionDecision.Allowed);
            ImGui.SameLine();

            bool canAlways = !entry.SuppressAlwaysAllow && entry.Suggestions is { ValueKind: System.Text.Json.JsonValueKind.Array } s && s.GetArrayLength() > 0;
            if (canAlways)
            {
                if (ImGui.Button("Always allow")) session.RespondToPermission(entry, PermissionDecision.AlwaysAllowed);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Allow and remember for this session");
                ImGui.SameLine();
            }

            if (ImGui.Button("Deny")) session.RespondToPermission(entry, PermissionDecision.Denied, "The user denied this.");
            if (entry.DefaultToNo) ImGui.SetItemDefaultFocus();
        }
        else
        {
            var (text, colour) = entry.Decision switch
            {
                PermissionDecision.Allowed       => ("Allowed", OkColour),
                PermissionDecision.AlwaysAllowed => ("Allowed for this session", OkColour),
                PermissionDecision.Denied        => ("Denied", ErrorColour),
                PermissionDecision.AutoDenied    => ("Denied automatically (no answer)", WarnColour),
                PermissionDecision.Withdrawn     => ("Withdrawn", DimColour),
                _                                => ("Pending (session ended)", DimColour),
            };
            ImGui.TextColored(colour, text);
        }

        ImGui.EndChild();
    }

    private static void DrawSystem(SystemEntry system)
    {
        var colour = system.Severity switch
        {
            SystemSeverity.Error   => ErrorColour,
            SystemSeverity.Warning => WarnColour,
            _                      => DimColour,
        };
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted("" + system.Text);
        ImGui.PopStyleColor();
    }

    private static void DrawResult(ResultEntry result)
    {
        if (result.IsError)
        {
            ImGui.TextColored(ErrorColour, $"Turn ended with an error{(result.Text != null ? ": " + result.Text : "")}");
            return;
        }

        ImGui.TextDisabled($"turn done · ${result.TotalCostUsd:F4} so far · {result.DurationMs / 1000.0:F1} s");
    }
}
