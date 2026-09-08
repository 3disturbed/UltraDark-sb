// -----------------------------------------------------------------------------
// GraphicsMenu — the engine's graphics settings screen.
//
// The mirror of SexyBiscuit.Engine/UI/GraphicsMenu.cs.
//
// Built from graphics-menu.json, which both engines read, rather than from a tree
// written out twice. A menu written twice is a menu where one engine grows a row the
// other never gets, and the point of this screen is that a player recognises it
// whichever build they are running.
//
// A row whose `requires` capability is false is not disabled, it is absent. This
// renderer has no shadow pass at all, and a shadow slider that visibly does nothing is
// worse than no shadow slider -- so the Advanced tab really is different on the two
// platforms, without either engine hard-coding what the other has.
// -----------------------------------------------------------------------------

import { UiCanvas } from './UiCanvas.js';
import { UiNode } from './UiNode.js';
import { UiKind, SizeMode, LayoutMode, AlignMode, PositionMode, UiAnchor, UiScaleMode, ScrollMode } from './UiEnums.js';
import { GraphicsSettings } from '../rendering/GraphicsSettings.js';
import { VERSION, NAME, statusLine } from '../core/EngineInfo.js';
import schema from './graphics-menu.json' with { type: 'json' };

/** Fields whose JSON key is not the property name. The C# side maps the other way. */
const FIELD_ALIASES = {};

export class GraphicsMenu {
    constructor(settings, capabilities) {
        this.settings = settings;
        this.capabilities = capabilities;
        this.schema = schema;

        /** Called when a setting changes, with the whole settings object. */
        this.onChanged = null;

        /** Called when the player asks for a benchmark run. */
        this.onBenchmark = null;

        /** Called when the menu is closed, so the caller can persist and unpause. */
        this.onClosed = null;

        /** Extra text shown on the benchmark tab, such as a result. */
        this.note = '';

        const layout = schema.layout;
        this.canvas = new UiCanvas();

        // Scaled rather than stretched, so the menu keeps its proportions at any window
        // size and a 4K screen does not render it at postage-stamp size.
        this.canvas.scaleMode = UiScaleMode.ScaleToFit;
        this.canvas.referenceResolution = { x: layout.referenceWidth, y: layout.referenceHeight };
        this.canvas.order = 1000;
        this.canvas.interactive = false;

        this._bindings = [];
        this._shownTab = -1;

        this._build();
        this._setOpen(false);
    }

    get isOpen() { return this.canvas.root.visible; }

    _setOpen(open) {
        this.canvas.root.visible = open;
        this.canvas.interactive = open;
    }

    /** Shows the menu and puts focus in it. */
    open() {
        this.refresh();
        this._setOpen(true);
        this.canvas.invalidateLayout();
    }

    /** Hides the menu and tells the caller. */
    close() {
        this._setOpen(false);
        this.onClosed?.();
    }

    /** Drops the canvas. A menu that outlives its game keeps painting over the next one. */
    destroy() { this.canvas.destroy(); }

    // -------------------------------------------------------------------------
    // The frame
    // -------------------------------------------------------------------------

    _build() {
        const { layout, theme } = schema;

        // A full-bleed scrim, so the game behind is dimmed and a click that misses the
        // panel lands on something rather than on the game.
        this.canvas.root.background = theme.scrim;
        this.canvas.root.layout = LayoutMode.None;

        const panel = this.canvas.root.add(new UiNode({
            name: 'panel',
            kind: UiKind.Panel,
            modal: true,
            positioning: PositionMode.Absolute,
            anchor: UiAnchor.Center,
            widthMode: SizeMode.Fixed, width: layout.panelWidth,
            heightMode: SizeMode.Fixed, height: layout.panelHeight,
            layout: LayoutMode.Column,
            gap: { x: 0, y: layout.gap },
            padding: { x: layout.padding, y: layout.padding, z: layout.padding, w: layout.padding },
            crossAlign: AlignMode.Stretch,
            background: theme.panel,
            borderColour: theme.border,
            borderWidth: 1,
        }));

        panel.add(new UiNode({
            name: 'title', kind: UiKind.Label, text: schema.title,
            heightMode: SizeMode.Fixed, height: layout.titleScale * 12,
            textScale: layout.titleScale, tint: theme.text,
        }));

        this._tabs = panel.add(new UiNode({
            name: 'tabs', kind: UiKind.TabStrip,
            heightMode: SizeMode.Fixed, height: layout.tabHeight,
            background: theme.control, tint: theme.accent,
        }));
        for (const tab of schema.tabs) this._tabs.options.push(tab.label);

        this._body = panel.add(new UiNode({
            name: 'body', kind: UiKind.ScrollView,
            grow: 1, scroll: ScrollMode.Vertical, clip: true,
            layout: LayoutMode.Column, gap: { x: 0, y: layout.gap },
            crossAlign: AlignMode.Stretch, tint: theme.accent,
        }));

        // The version line the whole menu is asked to carry, pinned to the foot of the
        // panel so it is on screen whichever tab is showing.
        this._status = panel.add(new UiNode({
            name: 'status', kind: UiKind.Label,
            heightMode: SizeMode.Fixed, height: layout.statusHeight,
            tint: theme.textDim,
        }));

        const close = panel.add(new UiNode({
            name: 'close', kind: UiKind.Button, text: 'CLOSE',
            heightMode: SizeMode.Fixed, height: layout.rowHeight,
            background: theme.control, tint: theme.text,
        }));
        this._bindings.push({ node: close, kind: 'close' });

        this._showTab(0);
    }

    // -------------------------------------------------------------------------
    // Tabs
    // -------------------------------------------------------------------------

    _showTab(index) {
        if (index < 0 || index >= schema.tabs.length) return;

        this._shownTab = index;
        this._tabs.selectedIndex = index;

        for (let i = this._body.children.length - 1; i >= 0; i--) {
            this._body.remove(this._body.children[i]);
        }
        this._bindings = this._bindings.filter((b) => b.kind === 'close');

        for (const row of schema.tabs[index].rows) {
            if (!this._isAvailable(row)) continue;

            if (row.kind === 'presetList') this._buildPresetList();
            else if (row.kind === 'benchmark') this._buildBenchmark();
            else this._buildRow(row);
        }

        this.refresh();
    }

    /**
     * Whether the running platform can honour this row at all.
     *
     * The one rule that makes the Advanced tab platform-specific. Absent rather than
     * greyed out: a control that is visibly present and permanently dead reads as a
     * bug, and this renderer genuinely has no shadow pass to attach one to.
     */
    _isAvailable(row) {
        if (!row.requires) return true;

        const known = ['shadows', 'postProcessing', 'lighting2D', 'anisotropy', 'displayControl', 'pixelRatio'];
        if (!known.includes(row.requires)) return false;   // Unknown is absent, never assumed.

        return Boolean(this.capabilities[row.requires]);
    }

    // -------------------------------------------------------------------------
    // Rows
    // -------------------------------------------------------------------------

    _newRow() {
        const { layout } = schema;
        return this._body.add(new UiNode({
            layout: LayoutMode.Row, gap: { x: 12, y: 0 },
            heightMode: SizeMode.Fixed, height: layout.rowHeight,
            crossAlign: AlignMode.Center,
        }));
    }

    _buildRow(row) {
        const { layout, theme } = schema;
        const line = this._newRow();

        line.add(new UiNode({
            kind: UiKind.Label,
            text: row.note ? `${row.label}  (${row.note})` : row.label,
            widthMode: SizeMode.Fixed, width: layout.labelWidth,
            heightMode: SizeMode.Stretch, tint: theme.textDim,
        }));

        let control;
        if (row.kind === 'toggle') {
            control = line.add(new UiNode({
                kind: UiKind.Toggle, grow: 1, heightMode: SizeMode.Stretch,
                background: theme.control, tint: theme.accent,
            }));
        } else if (row.kind === 'slider') {
            control = line.add(new UiNode({
                kind: UiKind.Slider, grow: 1, heightMode: SizeMode.Fixed, height: 20,
                minValue: row.min ?? 0, maxValue: row.max ?? 1, step: row.step ?? 0,
                background: theme.track, tint: theme.accent,
            }));
        } else {
            control = line.add(new UiNode({
                kind: UiKind.Dropdown, grow: 1, heightMode: SizeMode.Stretch,
                padding: { x: 8, y: 0, z: 8, w: 0 },
                background: theme.control, tint: theme.text,
            }));
        }

        control.name = row.field;

        // A slider carries its own readout, because a bare handle tells a player nothing
        // about whether they have chosen 0.75 or 0.8.
        let readout = null;
        if (row.kind === 'slider') {
            readout = line.add(new UiNode({
                kind: UiKind.Label, widthMode: SizeMode.Fixed, width: 70,
                heightMode: SizeMode.Stretch, textAlign: AlignMode.End, tint: theme.text,
            }));
        }

        const { labels, values } = optionsOf(row);
        for (const label of labels) control.options.push(label);

        this._bindings.push({ node: control, kind: 'field', row, values, readout });
    }

    _buildPresetList() {
        const { layout, theme } = schema;

        for (const preset of GraphicsSettings.presets) {
            const line = this._newRow();
            line.height = layout.rowHeight + 12;

            const button = line.add(new UiNode({
                name: `preset:${preset.id}`, kind: UiKind.Button, text: preset.name,
                widthMode: SizeMode.Fixed, width: layout.labelWidth,
                heightMode: SizeMode.Stretch,
                background: theme.control, tint: theme.text,
            }));

            line.add(new UiNode({
                kind: UiKind.Label, text: preset.summary ?? '', grow: 1,
                heightMode: SizeMode.Stretch, wrapText: true, tint: theme.textDim,
            }));

            this._bindings.push({ node: button, kind: 'preset', preset: preset.id });
        }
    }

    _buildBenchmark() {
        const { layout, theme } = schema;
        const line = this._newRow();

        const run = line.add(new UiNode({
            name: 'benchmark', kind: UiKind.Button, text: 'RUN BENCHMARK',
            widthMode: SizeMode.Fixed, width: layout.labelWidth,
            heightMode: SizeMode.Stretch, background: theme.control, tint: theme.text,
        }));
        this._bindings.push({ node: run, kind: 'benchmark' });

        line.add(new UiNode({
            kind: UiKind.Label, grow: 1, heightMode: SizeMode.Stretch, wrapText: true,
            text: 'Runs at the Benchmark preset with vsync off, then suggests a preset.',
            tint: theme.textDim,
        }));

        const result = this._body.add(new UiNode({
            name: 'benchmarkResult', kind: UiKind.Label,
            heightMode: SizeMode.Auto, wrapText: true, tint: theme.text,
        }));
        this._bindings.push({ node: result, kind: 'benchmarkResult' });
    }

    // -------------------------------------------------------------------------
    // The frame loop
    // -------------------------------------------------------------------------

    /**
     * Reads what the player did this frame and writes it back onto the settings.
     *
     * Called after the canvas's own input pass, so the click and value states it reads
     * are this frame's. A control's value is authoritative -- the settings follow the
     * UI rather than the other way round -- except immediately after a preset, which
     * rewrites everything and then pushes it back out through refresh().
     */
    tick() {
        if (!this.isOpen) return;

        if (this._tabs.selectedIndex !== this._shownTab) {
            this._showTab(this._tabs.selectedIndex);
            return;
        }

        let changed = false;

        for (const binding of this._bindings) {
            if (binding.kind === 'close' && binding.node.clicked) { this.close(); return; }
            if (binding.kind === 'benchmark' && binding.node.clicked) { this.onBenchmark?.(); return; }

            if (binding.kind === 'preset' && binding.node.clicked) {
                this.settings.applyPreset(binding.preset);
                this.onChanged?.(this.settings);
                this.refresh();
                return;
            }

            if (binding.kind === 'field' && this._pull(binding)) changed = true;
        }

        if (!changed) return;

        // Any hand-edited value means this is no longer any named preset.
        this.settings.preset = 'custom';
        this.onChanged?.(this.settings);
        this.refresh();
    }

    /** Pushes every setting back onto the controls, and rewrites the status line. */
    refresh() {
        for (const binding of this._bindings) {
            if (binding.kind === 'benchmarkResult') { binding.node.text = this.note; continue; }
            if (binding.kind !== 'field') continue;
            this._push(binding);
        }

        const preset = GraphicsSettings.findPreset(this.settings.preset);
        const adapter = this.capabilities?.adapter ?? 'Unknown';
        this._status.text = `${statusLine(this.capabilities?.webgl2 ? {} : null)} · ${adapter} · Preset: ${preset?.name ?? 'Custom'}`;
    }

    /** Replaces the settings and capabilities, rebuilding whatever that changes. */
    adopt(settings, capabilities) {
        this.settings = settings;
        this.capabilities = capabilities;
        this._showTab(this._shownTab < 0 ? 0 : this._shownTab);
    }

    // -------------------------------------------------------------------------
    // Binding
    // -------------------------------------------------------------------------

    _push(binding) {
        const { row, node, values, readout } = binding;
        const value = this.settings[fieldOf(row)];

        if (row.kind === 'toggle') {
            node.checked = Boolean(value);
        } else if (row.kind === 'slider') {
            node.value = Number(value) || 0;
            if (readout) readout.text = format(node.value, row.format);
        } else {
            node.selectedIndex = Math.max(0, values.findIndex((v) => same(v, value)));
        }
    }

    _pull(binding) {
        const { row, node, values, readout } = binding;
        const field = fieldOf(row);
        const was = this.settings[field];

        let now;
        if (row.kind === 'toggle') now = node.checked;
        else if (row.kind === 'slider') now = node.value;
        else now = values[node.selectedIndex];

        if (now === undefined || same(was, now)) return false;

        // Integer-typed fields must not pick up a float from a slider: a shadow map of
        // 1023.9997 is a texture allocation the driver will refuse.
        this.settings[field] = typeof was === 'number' && Number.isInteger(was)
            ? Math.round(Number(now))
            : now;

        if (row.kind === 'slider' && readout) readout.text = format(node.value, row.format);
        return true;
    }
}

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

function fieldOf(row) { return FIELD_ALIASES[row.field] ?? row.field; }

/**
 * A row's option labels and the values behind them.
 *
 * Two spellings, because an enum row's labels are its values and writing each one
 * twice in the file would be an invitation to mistype one.
 */
function optionsOf(row) {
    if (row.enum) return { labels: [...row.enum], values: [...row.enum] };
    if (row.options) return { labels: row.options.map((o) => o[0]), values: row.options.map((o) => o[1]) };
    return { labels: [], values: [] };
}

/**
 * Whether two values mean the same setting.
 *
 * Loose on purpose: a strict comparison would report a dropdown as changed on every
 * frame, and the menu would rewrite the settings and re-lay itself out sixty times a
 * second.
 */
function same(a, b) {
    if (a === null || a === undefined || b === null || b === undefined) return a === b;
    if (typeof a === 'number' || typeof b === 'number') {
        return Math.abs(Number(a) - Number(b)) < 0.0001;
    }
    if (typeof a === 'boolean' || typeof b === 'boolean') return Boolean(a) === Boolean(b);
    return String(a) === String(b);
}

/** The readout beside a slider. */
function format(value, style) {
    if (style === 'percent') return `${Math.round(value * 100)}%`;
    if (Math.abs(value - Math.round(value)) < 0.001) return String(Math.round(value));
    return value.toFixed(2);
}

export { VERSION, NAME };
