using System.Text.Json;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// The engine's graphics settings screen. The mirror of <c>html5/src/ui/GraphicsMenu.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built from <c>html5/src/ui/graphics-menu.json</c>, which both engines read, rather than
/// from a tree written out twice. A menu written twice is a menu where one engine grows a
/// row the other never gets, and the point of this screen is that a player recognises it
/// whichever build they are running.
/// </para>
/// <para>
/// A row whose <c>requires</c> capability is false is not disabled, it is absent. There is
/// no shadow pass in the browser renderer at all, and a shadow slider that visibly does
/// nothing is worse than no shadow slider — so the Advanced tab really is different on the
/// two platforms, without either engine hard-coding what the other has.
/// </para>
/// </remarks>
public sealed class GraphicsMenu
{
    private readonly UiCanvas   _canvas;
    private readonly MenuSchema _schema;

    private GraphicsCapabilities _capabilities;
    private GraphicsSettings     _settings;

    private UiNode  _tabs   = new();
    private UiNode  _body   = new();
    private UiNode  _status = new();
    private int     _shownTab = -1;

    private readonly List<Binding> _bindings = new();

    /// <summary>Raised when a setting changes, with the whole settings object.</summary>
    public Action<GraphicsSettings>? Changed { get; set; }

    /// <summary>Raised when the player asks for a benchmark run.</summary>
    public Action? BenchmarkRequested { get; set; }

    /// <summary>Raised when the menu is closed, so the caller can persist and unpause.</summary>
    public Action? Closed { get; set; }

    /// <summary>Extra text shown on the status line, such as a benchmark result.</summary>
    public string Note { get; set; } = "";

    /// <summary>The canvas this menu paints on. Ordered above anything a game builds.</summary>
    public UiCanvas Canvas => _canvas;

    /// <summary>Whether the menu is on screen.</summary>
    public bool IsOpen
    {
        get => _canvas.Root.Visible;
        private set { _canvas.Root.Visible = value; _canvas.Interactive = value; }
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public GraphicsMenu(GraphicsSettings settings, GraphicsCapabilities capabilities)
    {
        _settings     = settings ?? throw new ArgumentNullException(nameof(settings));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _schema       = MenuSchema.Shared;

        _canvas = new UiCanvas
        {
            // Scaled rather than stretched, so the menu keeps its proportions at any
            // window size and a 4K screen does not render it at postage-stamp size.
            ScaleMode           = UiScaleMode.ScaleToFit,
            ReferenceResolution = new Vector2(_schema.ReferenceWidth, _schema.ReferenceHeight),
            Order               = 1000,
            Interactive         = false,
        };

        Build();
        IsOpen = false;
    }

    /// <summary>Shows the menu and puts focus in it.</summary>
    public void Open()
    {
        Refresh();
        IsOpen = true;
        _canvas.InvalidateLayout();
    }

    /// <summary>Hides the menu and tells the caller.</summary>
    public void Close()
    {
        IsOpen = false;
        Closed?.Invoke();
    }

    /// <summary>Drops the canvas. A menu that outlives its game keeps painting over the next one.</summary>
    public void Destroy() => _canvas.OnDestroy();

    // -------------------------------------------------------------------------
    // The frame
    // -------------------------------------------------------------------------

    private void Build()
    {
        MenuTheme theme = _schema.Theme;

        // A full-bleed scrim, so the game behind is dimmed and a click that misses the
        // panel lands on something rather than on the game.
        _canvas.Root.Background = Colour(theme.Scrim);
        _canvas.Root.Layout     = LayoutMode.None;

        UiNode panel = _canvas.Root.Add(new UiNode
        {
            Name         = "panel",
            Kind         = UiKind.Panel,
            Modal        = true,
            Positioning  = PositionMode.Absolute,
            Anchor       = UiAnchor.Center,
            WidthMode    = SizeMode.Fixed,  Width  = _schema.PanelWidth,
            HeightMode   = SizeMode.Fixed,  Height = _schema.PanelHeight,
            Layout       = LayoutMode.Column,
            Gap          = new Vector2(0f, _schema.Gap),
            Padding      = new Vector4(_schema.Padding),
            CrossAlign   = AlignMode.Stretch,
            Background   = Colour(theme.Panel),
            BorderColour = Colour(theme.Border),
            BorderWidth  = 1f,
        });

        panel.Add(new UiNode
        {
            Name = "title", Kind = UiKind.Label, Text = _schema.Title,
            HeightMode = SizeMode.Fixed, Height = _schema.TitleScale * 12f,
            TextScale = _schema.TitleScale, Tint = Colour(theme.Text) ?? Color.White,
        });

        _tabs = panel.Add(new UiNode
        {
            Name = "tabs", Kind = UiKind.TabStrip,
            HeightMode = SizeMode.Fixed, Height = _schema.TabHeight,
            Background = Colour(theme.Control), Tint = Colour(theme.Accent) ?? Color.White,
        });
        foreach (MenuTab tab in _schema.Tabs) _tabs.Options.Add(tab.Label);

        _body = panel.Add(new UiNode
        {
            Name = "body", Kind = UiKind.ScrollView,
            Grow = 1f, Scroll = ScrollMode.Vertical, Clip = true,
            Layout = LayoutMode.Column, Gap = new Vector2(0f, _schema.Gap),
            CrossAlign = AlignMode.Stretch,
            Tint = Colour(theme.Accent) ?? Color.White,
        });

        // The version line the whole menu is asked to carry, pinned to the foot of the
        // panel so it is on screen whichever tab is showing.
        _status = panel.Add(new UiNode
        {
            Name = "status", Kind = UiKind.Label,
            HeightMode = SizeMode.Fixed, Height = _schema.StatusHeight,
            Tint = Colour(theme.TextDim) ?? Color.Gray,
        });

        UiNode close = panel.Add(new UiNode
        {
            Name = "close", Kind = UiKind.Button, Text = "CLOSE",
            HeightMode = SizeMode.Fixed, Height = _schema.RowHeight,
            Background = Colour(theme.Control), Tint = Colour(theme.Text) ?? Color.White,
        });
        _bindings.Add(new Binding(close, BindingKind.Close, "", null));

        ShowTab(0);
    }

    // -------------------------------------------------------------------------
    // Tabs
    // -------------------------------------------------------------------------

    private void ShowTab(int index)
    {
        if (index < 0 || index >= _schema.Tabs.Count) return;

        _shownTab = index;
        _tabs.SelectedIndex = index;

        for (int i = _body.Children.Count - 1; i >= 0; i--) _body.Remove(_body.Children[i]);
        _bindings.RemoveAll(b => b.Kind != BindingKind.Close);

        foreach (MenuRow row in _schema.Tabs[index].Rows)
        {
            if (!IsAvailable(row)) continue;

            switch (row.Kind)
            {
                case "presetList": BuildPresetList(); break;
                case "benchmark":  BuildBenchmark();  break;
                default:           BuildRow(row);     break;
            }
        }

        Refresh();
    }

    /// <summary>
    /// Whether the running platform can honour this row at all.
    /// </summary>
    /// <remarks>
    /// The one rule that makes the Advanced tab platform-specific. Absent rather than
    /// greyed out: a control that is visibly present and permanently dead reads as a bug,
    /// and this engine genuinely has no shadow pass in the browser to attach one to.
    /// </remarks>
    private bool IsAvailable(MenuRow row) => row.Requires switch
    {
        null or ""       => true,
        "shadows"        => _capabilities.Shadows,
        "postProcessing" => _capabilities.PostProcessing,
        "lighting2D"     => _capabilities.Lighting2D,
        "anisotropy"     => _capabilities.Anisotropy,
        "displayControl" => _capabilities.DisplayControl,
        "pixelRatio"     => _capabilities.PixelRatio,
        _                => false,   // An unknown capability is absent, never assumed.
    };

    // -------------------------------------------------------------------------
    // Rows
    // -------------------------------------------------------------------------

    private UiNode NewRow()
    {
        return _body.Add(new UiNode
        {
            Layout = LayoutMode.Row, Gap = new Vector2(12f, 0f),
            HeightMode = SizeMode.Fixed, Height = _schema.RowHeight,
            CrossAlign = AlignMode.Center,
        });
    }

    private void BuildRow(MenuRow row)
    {
        MenuTheme theme = _schema.Theme;
        UiNode line = NewRow();

        string label = string.IsNullOrEmpty(row.Note) ? row.Label : $"{row.Label}  ({row.Note})";
        line.Add(new UiNode
        {
            Kind = UiKind.Label, Text = label,
            WidthMode = SizeMode.Fixed, Width = _schema.LabelWidth,
            HeightMode = SizeMode.Stretch,
            Tint = Colour(theme.TextDim) ?? Color.Gray,
        });

        UiNode control = row.Kind switch
        {
            "toggle" => line.Add(new UiNode
            {
                Kind = UiKind.Toggle, Grow = 1f, HeightMode = SizeMode.Stretch,
                Background = Colour(theme.Control), Tint = Colour(theme.Accent) ?? Color.White,
            }),

            "slider" => line.Add(new UiNode
            {
                Kind = UiKind.Slider, Grow = 1f, HeightMode = SizeMode.Fixed, Height = 20f,
                MinValue = row.Min, MaxValue = row.Max, Step = row.Step,
                Background = Colour(theme.Track), Tint = Colour(theme.Accent) ?? Color.White,
            }),

            _ => line.Add(new UiNode
            {
                Kind = UiKind.Dropdown, Grow = 1f, HeightMode = SizeMode.Stretch,
                Padding = new Vector4(8f, 0f, 8f, 0f),
                Background = Colour(theme.Control), Tint = Colour(theme.Text) ?? Color.White,
            }),
        };

        control.Name = row.Field;

        // A slider carries its own readout, because a bare handle tells a player nothing
        // about whether they have chosen 0.75 or 0.8.
        UiNode? readout = null;
        if (row.Kind == "slider")
            readout = line.Add(new UiNode
            {
                Kind = UiKind.Label, WidthMode = SizeMode.Fixed, Width = 70f,
                HeightMode = SizeMode.Stretch, TextAlign = AlignMode.End,
                Tint = Colour(theme.Text) ?? Color.White,
            });

        foreach (string option in row.OptionLabels) control.Options.Add(option);

        _bindings.Add(new Binding(control, BindingKind.Field, row.Field, row) { Readout = readout });
    }

    private void BuildPresetList()
    {
        MenuTheme theme = _schema.Theme;

        foreach (GraphicsPreset preset in GraphicsSettings.Presets.Presets)
        {
            UiNode line = NewRow();
            line.Height = _schema.RowHeight + 12f;

            UiNode button = line.Add(new UiNode
            {
                Name = $"preset:{preset.Id}", Kind = UiKind.Button, Text = preset.Name,
                WidthMode = SizeMode.Fixed, Width = _schema.LabelWidth,
                HeightMode = SizeMode.Stretch,
                Background = Colour(theme.Control), Tint = Colour(theme.Text) ?? Color.White,
            });

            line.Add(new UiNode
            {
                Kind = UiKind.Label, Text = preset.Summary, Grow = 1f,
                HeightMode = SizeMode.Stretch, WrapText = true,
                Tint = Colour(theme.TextDim) ?? Color.Gray,
            });

            _bindings.Add(new Binding(button, BindingKind.Preset, preset.Id, null));
        }
    }

    private void BuildBenchmark()
    {
        MenuTheme theme = _schema.Theme;

        UiNode line = NewRow();
        UiNode run = line.Add(new UiNode
        {
            Name = "benchmark", Kind = UiKind.Button, Text = "RUN BENCHMARK",
            WidthMode = SizeMode.Fixed, Width = _schema.LabelWidth,
            HeightMode = SizeMode.Stretch,
            Background = Colour(theme.Control), Tint = Colour(theme.Text) ?? Color.White,
        });
        _bindings.Add(new Binding(run, BindingKind.Benchmark, "", null));

        line.Add(new UiNode
        {
            Kind = UiKind.Label, Grow = 1f, HeightMode = SizeMode.Stretch, WrapText = true,
            Text = "Runs at the Benchmark preset with vsync off, then suggests a preset.",
            Tint = Colour(theme.TextDim) ?? Color.Gray,
        });

        UiNode result = _body.Add(new UiNode
        {
            Name = "benchmarkResult", Kind = UiKind.Label,
            HeightMode = SizeMode.Auto, WrapText = true,
            Tint = Colour(theme.Text) ?? Color.White,
        });
        _bindings.Add(new Binding(result, BindingKind.BenchmarkResult, "", null));
    }

    // -------------------------------------------------------------------------
    // The frame loop
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads what the player did this frame and writes it back onto the settings.
    /// </summary>
    /// <remarks>
    /// Called after the canvas's own input pass, so the click and value states it reads
    /// are this frame's. A control's value is authoritative — the settings follow the UI
    /// rather than the other way round — except immediately after a preset, which
    /// rewrites everything and then pushes it back out through <see cref="Refresh"/>.
    /// </remarks>
    public void Tick()
    {
        if (!IsOpen) return;

        if (_tabs.SelectedIndex != _shownTab) { ShowTab(_tabs.SelectedIndex); return; }

        bool changed = false;

        foreach (Binding binding in _bindings)
        {
            switch (binding.Kind)
            {
                case BindingKind.Close when binding.Node.Clicked:
                    Close();
                    return;

                case BindingKind.Benchmark when binding.Node.Clicked:
                    BenchmarkRequested?.Invoke();
                    return;

                case BindingKind.Preset when binding.Node.Clicked:
                    _settings.ApplyPreset(binding.Field);
                    Changed?.Invoke(_settings);
                    Refresh();
                    return;

                case BindingKind.Field when Pull(binding):
                    changed = true;
                    break;
            }
        }

        if (!changed) return;

        // Any hand-edited value means this is no longer any named preset.
        _settings.Preset = "custom";
        Changed?.Invoke(_settings);
        Refresh();
    }

    /// <summary>Pushes every setting back onto the controls, and rewrites the status line.</summary>
    public void Refresh()
    {
        foreach (Binding binding in _bindings)
        {
            if (binding.Kind == BindingKind.BenchmarkResult) { binding.Node.Text = Note; continue; }
            if (binding.Kind != BindingKind.Field || binding.Row is null) continue;

            Push(binding);
        }

        GraphicsPreset? preset = GraphicsSettings.Presets.Find(_settings.Preset);
        string name = preset?.Name ?? "Custom";
        _status.Text = $"{EngineInfo.StatusLine} · {_capabilities.Adapter} · Preset: {name}";
    }

    /// <summary>Replaces the settings and capabilities, rebuilding whatever that changes.</summary>
    public void Adopt(GraphicsSettings settings, GraphicsCapabilities capabilities)
    {
        _settings     = settings ?? throw new ArgumentNullException(nameof(settings));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        ShowTab(_shownTab < 0 ? 0 : _shownTab);
    }

    // -------------------------------------------------------------------------
    // Binding
    // -------------------------------------------------------------------------

    private enum BindingKind { Field, Preset, Close, Benchmark, BenchmarkResult }

    private sealed record Binding(UiNode Node, BindingKind Kind, string Field, MenuRow? Row)
    {
        public UiNode? Readout { get; init; }
    }

    /// <summary>Writes a setting onto its control.</summary>
    private void Push(Binding binding)
    {
        MenuRow row = binding.Row!;
        object? value = MenuFields.Read(_settings, row.Field);

        switch (row.Kind)
        {
            case "toggle":
                binding.Node.Checked = value is true;
                break;

            case "slider":
                binding.Node.Value = Convert.ToSingle(value ?? 0f);
                if (binding.Readout != null) binding.Readout.Text = MenuFields.Format(binding.Node.Value, row.Format);
                break;

            default:
                binding.Node.SelectedIndex = Math.Max(0, row.IndexOf(value));
                break;
        }
    }

    /// <summary>Reads a control back onto the settings. True when it actually changed.</summary>
    private bool Pull(Binding binding)
    {
        MenuRow row = binding.Row!;
        object? was = MenuFields.Read(_settings, row.Field);

        object? now = row.Kind switch
        {
            "toggle" => binding.Node.Checked,
            "slider" => binding.Node.Value,
            _        => row.ValueAt(binding.Node.SelectedIndex),
        };

        if (now is null || MenuFields.Same(was, now)) return false;

        MenuFields.Write(_settings, row.Field, now);
        if (row.Kind == "slider" && binding.Readout != null)
            binding.Readout.Text = MenuFields.Format(binding.Node.Value, row.Format);

        return true;
    }

    private static Color? Colour(string hex) => UiDocument.ParseColour(hex);

    // -------------------------------------------------------------------------
    // The schema
    // -------------------------------------------------------------------------

    /// <summary>One row of the menu, as the shared description states it.</summary>
    public sealed class MenuRow
    {
        public string  Kind     { get; init; } = "";
        public string  Field    { get; init; } = "";
        public string  Label    { get; init; } = "";
        public string? Requires { get; init; }
        public string  Note     { get; init; } = "";
        public string  Format   { get; init; } = "";
        public float   Min      { get; init; }
        public float   Max      { get; init; } = 1f;
        public float   Step     { get; init; }

        /// <summary>The labels a dropdown shows.</summary>
        public IReadOnlyList<string> OptionLabels { get; init; } = Array.Empty<string>();

        /// <summary>The values behind those labels, boxed as they came out of the JSON.</summary>
        public IReadOnlyList<object> OptionValues { get; init; } = Array.Empty<object>();

        /// <summary>The option index holding a value, or -1.</summary>
        public int IndexOf(object? value)
        {
            for (int i = 0; i < OptionValues.Count; i++)
                if (MenuFields.Same(OptionValues[i], value)) return i;
            return -1;
        }

        /// <summary>The value at an index, or null when there is none.</summary>
        public object? ValueAt(int index)
            => index >= 0 && index < OptionValues.Count ? OptionValues[index] : null;
    }

    /// <summary>One tab of the menu.</summary>
    public sealed class MenuTab
    {
        public string Id    { get; init; } = "";
        public string Label { get; init; } = "";
        public IReadOnlyList<MenuRow> Rows { get; init; } = Array.Empty<MenuRow>();
    }

    /// <summary>The menu's colours, as the shared description states them.</summary>
    public sealed record MenuTheme(string Scrim, string Panel, string Border, string Control,
                                   string Accent, string Text, string TextDim, string Track);

    /// <summary>The shared menu description, parsed once.</summary>
    public sealed class MenuSchema
    {
        /// <summary>The description both engines read.</summary>
        public static MenuSchema Shared { get; } = Load();

        public string Title { get; private init; } = "Graphics";
        public IReadOnlyList<MenuTab> Tabs { get; private init; } = Array.Empty<MenuTab>();
        public MenuTheme Theme { get; private init; }
            = new("#0b0d12cc", "#161920", "#2a2f3a", "#232833", "#7fa650", "#e8e6e1", "#9a978f", "#11141a");

        public float ReferenceWidth  { get; private init; } = 1280f;
        public float ReferenceHeight { get; private init; } = 720f;
        public float PanelWidth      { get; private init; } = 760f;
        public float PanelHeight     { get; private init; } = 560f;
        public float RowHeight       { get; private init; } = 34f;
        public float LabelWidth      { get; private init; } = 260f;
        public float Gap             { get; private init; } = 6f;
        public float Padding         { get; private init; } = 20f;
        public float TitleScale      { get; private init; } = 3f;
        public float TabHeight       { get; private init; } = 34f;
        public float StatusHeight    { get; private init; } = 16f;

        private static MenuSchema Load()
        {
            using Stream? stream = typeof(MenuSchema).Assembly
                .GetManifestResourceStream("SexyBiscuit.Engine.graphics-menu.json");

            // An empty menu rather than a throw: a broken build should render, not refuse.
            if (stream is null) return new MenuSchema();

            using var document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            JsonElement layout = root.GetProperty("layout");
            JsonElement theme  = root.GetProperty("theme");

            var tabs = new List<MenuTab>();
            foreach (JsonElement tab in root.GetProperty("tabs").EnumerateArray())
            {
                var rows = new List<MenuRow>();
                foreach (JsonElement row in tab.GetProperty("rows").EnumerateArray()) rows.Add(ReadRow(row));

                tabs.Add(new MenuTab
                {
                    Id    = tab.GetProperty("id").GetString() ?? "",
                    Label = tab.GetProperty("label").GetString() ?? "",
                    Rows  = rows,
                });
            }

            return new MenuSchema
            {
                Title = root.GetProperty("title").GetString() ?? "Graphics",
                Tabs  = tabs,
                Theme = new MenuTheme(
                    Str(theme, "scrim"), Str(theme, "panel"), Str(theme, "border"), Str(theme, "control"),
                    Str(theme, "accent"), Str(theme, "text"), Str(theme, "textDim"), Str(theme, "track")),
                ReferenceWidth  = Num(layout, "referenceWidth",  1280f),
                ReferenceHeight = Num(layout, "referenceHeight", 720f),
                PanelWidth      = Num(layout, "panelWidth",      760f),
                PanelHeight     = Num(layout, "panelHeight",     560f),
                RowHeight       = Num(layout, "rowHeight",       34f),
                LabelWidth      = Num(layout, "labelWidth",      260f),
                Gap             = Num(layout, "gap",             6f),
                Padding         = Num(layout, "padding",         20f),
                TitleScale      = Num(layout, "titleScale",      3f),
                TabHeight       = Num(layout, "tabHeight",       34f),
                StatusHeight    = Num(layout, "statusHeight",    16f),
            };
        }

        private static MenuRow ReadRow(JsonElement row)
        {
            var labels = new List<string>();
            var values = new List<object>();

            // Two spellings, because an enum row's labels are its values and writing
            // each one twice in the file would be an invitation to mistype one.
            if (row.TryGetProperty("enum", out JsonElement members))
                foreach (JsonElement member in members.EnumerateArray())
                {
                    labels.Add(member.GetString() ?? "");
                    values.Add(member.GetString() ?? "");
                }

            if (row.TryGetProperty("options", out JsonElement options))
                foreach (JsonElement option in options.EnumerateArray())
                {
                    labels.Add(option[0].GetString() ?? "");
                    values.Add(option[1].ValueKind == JsonValueKind.Number
                        ? option[1].GetDouble()
                        : option[1].GetString() ?? "");
                }

            return new MenuRow
            {
                Kind         = row.GetProperty("kind").GetString() ?? "",
                Field        = row.TryGetProperty("field", out JsonElement f) ? f.GetString() ?? "" : "",
                Label        = row.TryGetProperty("label", out JsonElement l) ? l.GetString() ?? "" : "",
                Requires     = row.TryGetProperty("requires", out JsonElement r) ? r.GetString() : null,
                Note         = row.TryGetProperty("note", out JsonElement n) ? n.GetString() ?? "" : "",
                Format       = row.TryGetProperty("format", out JsonElement fm) ? fm.GetString() ?? "" : "",
                Min          = Num(row, "min", 0f),
                Max          = Num(row, "max", 1f),
                Step         = Num(row, "step", 0f),
                OptionLabels = labels,
                OptionValues = values,
            };
        }

        private static float Num(JsonElement element, string name, float fallback)
            => element.TryGetProperty(name, out JsonElement value) ? value.GetSingle() : fallback;

        private static string Str(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";
    }
}
