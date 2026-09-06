// -----------------------------------------------------------------------------
// Assets — the project's files, and the scenes it contains.
//
// A browser cannot list a directory, so the panel builds its tree from what the
// project actually references — the scenes, scripts and textures the loaded
// scene names — plus anything the user opens through the file picker. That is
// less than a native content browser shows, and it is what a page served over
// HTTP can honestly know.
// -----------------------------------------------------------------------------

import { el, clear, truncate } from '../dom.js';

/** The content browser. */
export class AssetsPanel {
    constructor(state, editor) {
        this.state = state;
        this.editor = editor;
        this.root = el('div.sb-panel-body.sb-assets');

        /** @type {Map<string, {kind: string, path: string}>} */
        this._known = new Map();

        state.sceneChanged.add(() => this.refresh());
        state.projectOpened.add(() => this.refresh());
        this.render();
    }

    /** Re-derives the asset list from the open scene. */
    refresh() {
        const scene = this.editor.scene;
        if (!scene) { this.render(); return; }

        for (const actor of scene.allActors) {
            for (const component of actor.getAllComponents()) {
                for (const [key, value] of Object.entries(component)) {
                    if (typeof value !== 'string' || value.length === 0) continue;
                    if (!/(path|Path)$/.test(key)) continue;
                    this._known.set(value, { kind: kindOf(value), path: value });
                }
            }
        }

        if (this.state.currentScenePath) {
            this._known.set(this.state.currentScenePath, {
                kind: 'scene', path: this.state.currentScenePath,
            });
        }

        this.render();
    }

    render() {
        clear(this.root);

        this.root.append(el('div.sb-assets-bar', {},
            el('button.sb-btn.sb-btn-small', {
                type: 'button', text: 'Open scene file…',
                onclick: () => this.editor.openSceneFromDisk(),
            }),
            el('button.sb-btn.sb-btn-small', {
                type: 'button', text: 'Open project folder…',
                title: 'Pick a folder containing ProjectSettings.json',
                onclick: () => this.editor.openProjectFromDisk(),
            })));

        if (this.state.projectRoot) {
            this.root.append(el('div.sb-assets-root', {
                text: this.state.projectRoot,
                title: this.state.projectRoot,
            }));
        }

        const entries = [...this._known.values()].sort(
            (a, b) => a.kind.localeCompare(b.kind) || a.path.localeCompare(b.path));

        if (entries.length === 0) {
            this.root.append(el('div.sb-empty', {
                text: 'Assets referenced by the open scene appear here.',
            }));
            return;
        }

        let lastKind = null;
        for (const entry of entries) {
            if (entry.kind !== lastKind) {
                lastKind = entry.kind;
                this.root.append(el('div.sb-palette-category', { text: `${entry.kind}s` }));
            }

            this.root.append(el('button.sb-asset-row', {
                type: 'button',
                title: entry.path,
                onclick: () => this._select(entry),
                ondblclick: () => this._open(entry),
            },
                el('span.sb-asset-icon', { text: iconFor(entry.kind) }),
                el('span.sb-asset-name', { text: truncate(entry.path, 44) })));
        }
    }

    _select(entry) {
        this.state.selectedAssetPath = entry.path;
        for (const row of this.root.querySelectorAll('.sb-asset-row')) {
            row.classList.toggle('is-selected', row.title === entry.path);
        }
    }

    _open(entry) {
        if (entry.kind === 'scene') this.editor.loadScene(entry.path);
        else this.state.info(`${entry.path} — open it in your editor of choice.`);
    }
}

function kindOf(path) {
    const lower = path.toLowerCase();
    if (lower.endsWith('.scene')) return 'scene';
    if (lower.endsWith('.js')) return 'script';
    if (/\.(png|jpe?g|gif|webp|svg)$/.test(lower)) return 'texture';
    if (/\.(wav|mp3|ogg|m4a)$/.test(lower)) return 'audio';
    if (/\.(gltf|glb|obj|fbx)$/.test(lower)) return 'model';
    return 'file';
}

function iconFor(kind) {
    return {
        scene: '▦', script: '⌨', texture: '▣', audio: '♪', model: '◆',
    }[kind] ?? '·';
}
