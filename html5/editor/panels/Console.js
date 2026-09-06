// -----------------------------------------------------------------------------
// Console — the log, and a JavaScript prompt bound to the live scene.
// -----------------------------------------------------------------------------

import { el, clear } from '../dom.js';

const MAX_LINES = 500;

/** The output log and the REPL. */
export class ConsolePanel {
    constructor(state, editor) {
        this.state = state;
        this.editor = editor;

        this.root = el('div.sb-panel-body.sb-console');
        this._output = el('div.sb-console-output');
        this._entries = [];
        this._filter = new Set(['info', 'warn', 'error', 'echo']);
        this._history = [];
        this._historyIndex = -1;

        this._build();
        state.logged.add((entry) => this.append(entry));
        this._captureConsole();
    }

    _build() {
        const toolbar = el('div.sb-console-bar', {},
            ...['info', 'warn', 'error'].map((level) => el('button.sb-chip', {
                type: 'button',
                text: level,
                class: 'is-on',
                onclick: (e) => {
                    if (this._filter.has(level)) this._filter.delete(level);
                    else this._filter.add(level);
                    e.target.classList.toggle('is-on', this._filter.has(level));
                    this._render();
                },
            })),
            el('button.sb-chip.sb-chip-clear', {
                type: 'button', text: 'Clear',
                onclick: () => { this._entries.length = 0; this._render(); },
            }));

        const prompt = el('input.sb-console-input', {
            type: 'text',
            placeholder: 'Evaluate JavaScript — scene, editor and engine are in scope',
            spellcheck: false,
            onkeydown: (e) => this._onPromptKey(e),
        });

        this.root.append(toolbar, this._output, el('div.sb-console-prompt', {},
            el('span.sb-prompt-caret', { text: '›' }), prompt));

        this._prompt = prompt;
    }

    /** Adds one line and trims the backlog. */
    append(entry) {
        this._entries.push(entry);
        if (this._entries.length > MAX_LINES) this._entries.splice(0, this._entries.length - MAX_LINES);
        this._render();
    }

    _render() {
        clear(this._output);

        for (const entry of this._entries) {
            if (!this._filter.has(entry.level)) continue;
            this._output.append(el(`div.sb-line.is-${entry.level}`, {},
                el('span.sb-line-time', { text: formatTime(entry.time) }),
                el('span.sb-line-text', { text: entry.message })));
        }

        // Follow the tail: a log you have to scroll to read is a log nobody reads.
        this._output.scrollTop = this._output.scrollHeight;
    }

    _onPromptKey(event) {
        if (event.key === 'ArrowUp') {
            event.preventDefault();
            this._recall(-1);
            return;
        }
        if (event.key === 'ArrowDown') {
            event.preventDefault();
            this._recall(1);
            return;
        }
        if (event.key !== 'Enter') return;

        const source = this._prompt.value.trim();
        if (!source) return;

        this._prompt.value = '';
        this._history.push(source);
        this._historyIndex = this._history.length;

        this.append({ level: 'echo', message: `› ${source}`, time: new Date() });
        this._evaluate(source);
    }

    _recall(direction) {
        if (this._history.length === 0) return;
        this._historyIndex = Math.max(0, Math.min(this._history.length, this._historyIndex + direction));
        this._prompt.value = this._history[this._historyIndex] ?? '';
    }

    /**
     * Runs a line against the live scene.
     *
     * The expression is compiled with the editor's objects as named parameters,
     * so `scene.findByName('Player')` works without anything being put on the
     * global object.
     */
    _evaluate(source) {
        const context = {
            scene: this.editor.scene,
            editor: this.editor,
            engine: this.editor.engine,
            state: this.state,
            selected: this.state.selectedActor,
            SB: this.editor.api,
        };

        try {
            // Wrapped in `return (...)` so a bare expression yields its value;
            // a statement falls back to the block form below.
            const names = Object.keys(context);
            let fn;
            try {
                // eslint-disable-next-line no-new-func
                fn = new Function(...names, `return (${source});`);
            } catch {
                // eslint-disable-next-line no-new-func
                fn = new Function(...names, source);
            }

            const result = fn(...names.map((n) => context[n]));
            this.append({ level: 'info', message: describe(result), time: new Date() });

            // A command routinely changes the world; refresh what shows it.
            this.state.notifyHierarchy();
            this.state.markDirty();
        } catch (err) {
            this.append({ level: 'error', message: String(err?.message ?? err), time: new Date() });
        }
    }

    /**
     * Mirrors `console.*` into the panel.
     *
     * The engine reports script errors and asset failures through the browser
     * console; without this they land in devtools, which is not where someone
     * editing a scene on a tablet is looking.
     */
    _captureConsole() {
        for (const [method, level] of [['log', 'info'], ['warn', 'warn'], ['error', 'error']]) {
            const original = console[method].bind(console);
            console[method] = (...args) => {
                original(...args);
                this.append({ level, message: args.map(describe).join(' '), time: new Date() });
            };
        }
    }
}

function describe(value) {
    if (typeof value === 'string') return value;
    if (value === undefined) return 'undefined';
    if (value === null) return 'null';
    if (value instanceof Error) return `${value.name}: ${value.message}`;
    if (typeof value === 'function') return `[function ${value.name || 'anonymous'}]`;

    if (typeof value === 'object') {
        if (typeof value.toString === 'function' && value.toString !== Object.prototype.toString) {
            return value.toString();
        }
        try { return JSON.stringify(value); } catch { return '[object]'; }
    }

    return String(value);
}

function formatTime(date) {
    return date.toTimeString().slice(0, 8);
}
