// -----------------------------------------------------------------------------
// Editor — the application: panels, the engine it hosts, and the actions.
//
// The editor hosts an EngineHost directly and edits live Actor objects by
// reference, exactly as the C# editor does. Play mode round-trips the scene
// through the serialiser: the edit-time scene is snapshotted, a fresh copy is
// played, and the snapshot is restored on stop, so playing can never leave marks
// on the file being edited.
// -----------------------------------------------------------------------------

import { el, clear } from './dom.js';
import { EditorState } from './EditorState.js';
import { HierarchyPanel } from './panels/Hierarchy.js';
import { InspectorPanel } from './panels/Inspector.js';
import { ViewportPanel } from './panels/Viewport.js';
import { ConsolePanel } from './panels/Console.js';
import { PalettePanel } from './panels/Palette.js';
import { ChibiPanel } from './panels/Chibi.js';
import { AssetsPanel } from './panels/Assets.js';
import { EditorHistory } from './EditorHistory.js';

import * as SB from '../src/index.js';
import { EngineHost, EngineConfig } from '../src/EngineHost.js';
import { PlayMode } from '../src/core/PlayMode.js';
import { ExceptionIsolation } from '../src/core/ExceptionIsolation.js';
import { serialize, deserialize, buildActorDto, buildActor } from '../src/scene/SceneSerializer.js';
import { createDefault2D, createDefault3D, createEmpty } from '../src/scene/SceneTemplates.js';
import { Camera3D } from '../src/rendering/Camera3D.js';
import { Transform3D } from '../src/core/Transform3D.js';
import { Vector3 } from '../src/math/index.js';

/** The SexyBiscuit HTML5 editor. */
export class Editor {
    constructor({ mount = null, projectRoot = '' } = {}) {
        this.mount = mount ?? document.getElementById('editor') ?? document.body;
        this.state = new EditorState();
        this.state.projectRoot = projectRoot;

        /** The whole engine API, so the console prompt can reach it. */
        this.api = SB;

        this.engine = null;
        this._sceneSnapshot = null;
        this._directoryHandle = null;
        this.history = new EditorHistory(this);
    }

    /** The scene being edited or played. */
    get scene() { return this.engine?.sceneManager.activeScene ?? null; }

    // -------------------------------------------------------------------------
    // Boot
    // -------------------------------------------------------------------------

    /** Builds the interface, starts the engine and opens a starting scene. */
    async start() {
        this._buildLayout();

        // Nothing spawns until Play: opening a scene must not populate it with
        // controllers and pawns that would then be written into the file.
        PlayMode.isActive = false;

        // One throwing component must not take the editor down with it.
        ExceptionIsolation.setHandler((error, owner, phase) => {
            this.state.error(`${owner?.constructor?.name ?? 'object'}.${phase}: ${error.message}`);
            return true;
        });

        this.engine = new EngineHost({
            mount: this.viewport.surface,
            assetRoot: this.state.projectRoot,
            config: new EngineConfig({ showCursor: true, clearColour: '#1a1d24' }),
        });

        this.viewport.attach(this.engine);
        this.engine.input.preventDefaults = false;   // the editor's own UI needs its keys

        // Render, but do not simulate, until Play. Otherwise a scene starts
        // falling apart the moment it is opened.
        this.engine.simulate = false;

        this.engine.onFrame = () => {
            this.viewport.syncRenderCamera();
            this.viewport.drawOverlay();
            this._updateStatus();
        };

        this.newScene('3d');
        this.engine.start();

        this._installShortcuts();
        this.state.info('Editor ready. Drop a .scene file onto the viewport, or pick a template.');

        return this;
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    _buildLayout() {
        clear(this.mount);

        this.viewport = new ViewportPanel(this.state, this);
        this.hierarchy = new HierarchyPanel(this.state, this);
        this.inspector = new InspectorPanel(this.state, this);
        this.console = new ConsolePanel(this.state, this);
        this.palette = new PalettePanel(this.state, this);
        this.chibi = new ChibiPanel(this.state, this);
        this.assets = new AssetsPanel(this.state, this);

        this._status = el('span.sb-status-text');

        this.mount.append(
            this._toolbar(),
            el('div.sb-body', {},
                this._dock('left', [
                    ['Place actors', this.palette.root],
                    ['Hierarchy', this.hierarchy.root],
                    ['MakeChibi', this.chibi.root],
                ]),
                el('div.sb-centre', {},
                    this.viewport.root,
                    this._dock('bottom', [
                        ['Console', this.console.root],
                        ['Assets', this.assets.root],
                    ])),
                this._dock('right', [
                    ['Inspector', this.inspector.root],
                ])),
            el('footer.sb-statusbar', {}, this._status));

        this._buildCommandPalette();

        this._installDropTarget();
    }

    /**
     * A dock of tabbed panels.
     *
     * Tabs rather than a resizable node graph: this has to be usable on a tablet,
     * where a drag-to-dock layout is unmanageable and every pixel of a panel that
     * is not the one you are using is wasted.
     */
    _dock(position, panels) {
        const dock = el(`div.sb-dock.sb-dock-${position}`);
        const tabs = el('div.sb-tabs');
        const body = el('div.sb-dock-body');

        panels.forEach(([title, content], index) => {
            const tab = el('button.sb-tab', {
                type: 'button',
                text: title,
                onclick: () => {
                    for (const other of tabs.children) other.classList.remove('is-active');
                    tab.classList.add('is-active');
                    clear(body).append(content);
                },
            });

            if (index === 0) { tab.classList.add('is-active'); body.append(content); }
            tabs.append(tab);
        });

        dock.append(tabs, body);
        return dock;
    }

    _toolbar() {
        const button = (label, title, onclick, extra = '') =>
            el(`button.sb-btn${extra}`, { type: 'button', text: label, title, onclick });

        this._playButton = button('▶', 'Play  (F5)', () => this.togglePlay(), '.sb-btn-play');
        this._pauseButton = button('❙❙', 'Pause  (F6)', () => this.togglePause());
        this._stopButton = button('■', 'Stop  (F7)', () => this.stop());

        return el('header.sb-toolbar', {},
            el('span.sb-brand', { text: 'SexyBiscuit' }),

            el('div.sb-toolgroup', {},
                el('select.sb-select', {
                    title: 'New scene from a template',
                    onchange: (e) => { this.newScene(e.target.value); e.target.value = ''; },
                },
                    el('option', { value: '', text: 'New scene…' }),
                    el('option', { value: '3d', text: '3D scene' }),
                    el('option', { value: '2d', text: '2D scene' }),
                    el('option', { value: 'empty', text: 'Empty' })),
                button('Open', 'Open a .scene file', () => this.openSceneFromDisk()),
                button('Save', 'Download the scene  (Ctrl/Cmd+S)', () => this.saveScene())),

            el('div.sb-toolgroup.sb-transport', {},
                this._playButton, this._pauseButton, this._stopButton),

            el('div.sb-toolgroup', {},
                button('⌘P', 'Command palette  (Ctrl/Cmd+P)', () => this.toggleCommandPalette())),

            el('div.sb-toolgroup.sb-transform-tools', {},
                button('W', 'Move selection  (W)', () => { this.state.gizmoMode = 'translate'; }),
                button('E', 'Rotate selection  (E)', () => { this.state.gizmoMode = 'rotate'; }),
                button('R', 'Scale selection  (R)', () => { this.state.gizmoMode = 'scale'; }),
                button('World', 'Toggle world/local transform space', () => {
                    this.state.transformSpace = this.state.transformSpace === 'world' ? 'local' : 'world';
                }),
                button('Snap', 'Toggle transform snapping', () => {
                    this.state.snapEnabled = !this.state.snapEnabled;
                })),

            el('div.sb-toolgroup', {},
                el('label.sb-toggle', {},
                    el('input', {
                        type: 'checkbox', checked: true,
                        onchange: (e) => { this.state.viewport3D = e.target.checked; },
                    }), '3D'),
                el('label.sb-toggle', {},
                    el('input', {
                        type: 'checkbox',
                        title: 'Look through the scene’s own camera',
                        onchange: (e) => { this.state.useGameCamera = e.target.checked; },
                    }), 'Game camera')));
    }

    /** Named commands are the browser workbench's single dispatch surface. */
    commandDefinitions() {
        const selected = () => Boolean(this.state.selectedActor);
        return [
            { id: 'editor.transform.translate', label: 'Move Selection', shortcut: 'W',
                run: () => { this.state.gizmoMode = 'translate'; } },
            { id: 'editor.transform.rotate', label: 'Rotate Selection', shortcut: 'E',
                run: () => { this.state.gizmoMode = 'rotate'; } },
            { id: 'editor.transform.scale', label: 'Scale Selection', shortcut: 'R',
                run: () => { this.state.gizmoMode = 'scale'; } },
            { id: 'editor.focus', label: 'Focus Selection', shortcut: 'F', enabled: selected,
                run: () => this.focusOnActor(this.state.selectedActor) },
            { id: 'editor.undo', label: 'Undo', shortcut: 'Ctrl/Cmd+Z', enabled: () => this.history.canUndo,
                run: () => this.history.undo() },
            { id: 'editor.redo', label: 'Redo', shortcut: 'Ctrl/Cmd+Y', enabled: () => this.history.canRedo,
                run: () => this.history.redo() },
            { id: 'editor.duplicate', label: 'Duplicate Selection', shortcut: 'Ctrl/Cmd+D', enabled: selected,
                run: () => this.duplicateActor(this.state.selectedActor) },
            { id: 'editor.delete', label: 'Delete Selection', shortcut: 'Delete', enabled: selected,
                run: () => this.deleteActor(this.state.selectedActor) },
            { id: 'editor.play.toggle', label: this.state.isPlaying ? 'Stop Play' : 'Play', shortcut: 'F5',
                run: () => this.togglePlay() },
            { id: 'editor.save', label: 'Save Scene', shortcut: 'Ctrl/Cmd+S', run: () => this.saveScene() },
        ];
    }

    executeCommand(id) {
        const command = this.commandDefinitions().find((entry) => entry.id === id);
        if (!command || command.enabled?.() === false) return false;
        command.run();
        return true;
    }

    _buildCommandPalette() {
        const query = el('input.sb-command-query', {
            type: 'search', placeholder: 'Type a command…', autocomplete: 'off',
        });
        const results = el('div.sb-command-results');
        const root = el('div.sb-command-overlay', { role: 'dialog', 'aria-label': 'Command palette' },
            el('section.sb-command-palette', {},
                el('header.sb-command-head', {},
                    el('span', { text: 'Command Palette' }),
                    el('kbd', { text: 'Esc' })),
                query,
                results));
        this.mount.append(root);

        this._commandPalette = { root, query, results };
        query.addEventListener('input', () => this._renderCommandPalette());
        query.addEventListener('keydown', (event) => {
            if (event.key !== 'Enter') return;
            const first = results.querySelector('button:not(:disabled)');
            first?.click();
        });
        root.addEventListener('pointerdown', (event) => {
            if (event.target === root) this.toggleCommandPalette(false);
        });
    }

    toggleCommandPalette(force = null) {
        const palette = this._commandPalette;
        if (!palette) return;
        const open = force ?? !palette.root.classList.contains('is-open');
        palette.root.classList.toggle('is-open', open);
        if (!open) return;
        palette.query.value = '';
        this._renderCommandPalette();
        palette.query.focus();
    }

    _renderCommandPalette() {
        const palette = this._commandPalette;
        if (!palette) return;
        const term = palette.query.value.trim().toLowerCase();
        const commands = this.commandDefinitions().filter((command) =>
            !term || `${command.label} ${command.id} ${command.shortcut}`.toLowerCase().includes(term));
        clear(palette.results);
        for (const command of commands) {
            const enabled = command.enabled?.() !== false;
            palette.results.append(el('button.sb-command-row', {
                type: 'button', disabled: !enabled,
                onclick: () => {
                    if (this.executeCommand(command.id)) this.toggleCommandPalette(false);
                },
            }, el('span', { text: command.label }),
            el('span.sb-command-shortcut', { text: command.shortcut })));
        }
        if (!commands.length) palette.results.append(el('div.sb-empty', { text: 'No matching command.' }));
    }

    _updateStatus() {
        const scene = this.scene;
        const stats = this.engine?.renderer3D?.stats;

        this._status.textContent = [
            scene ? `${scene.name}${this.state.sceneDirty ? ' •' : ''}` : 'No scene',
            scene ? `${scene.allActors.length} actors` : null,
            `${SB.Time.fps.toFixed(0)} fps`,
            stats ? `${stats.renderersDrawn}/${stats.renderersTotal} drawn` : null,
            this.state.isPlaying ? (this.state.isPaused ? 'paused' : 'playing') : 'editing',
        ].filter(Boolean).join('   ·   ');
    }

    // -------------------------------------------------------------------------
    // Scene actions
    // -------------------------------------------------------------------------

    /** Replaces the open scene with a fresh one from a template. */
    newScene(template = '3d') {
        if (!template) return;

        const builders = { '3d': createDefault3D, '2d': createDefault2D, empty: createEmpty };
        const scene = (builders[template] ?? createEmpty)('Untitled');

        this._adopt(scene);
        this.history.clear();
        this.state.currentScenePath = null;
        this.state.markClean();
        this.state.info(`New ${template} scene.`);
    }

    /** Loads a scene by project-relative path. */
    async loadScene(path) {
        try {
            const text = await this.engine.assets.loadText(path);
            this._adopt(deserialize(text, { flush: false, onWarning: (m) => this.state.warn(m) }));
            this.history.clear();
            this.state.currentScenePath = path;
            this.state.markClean();
            this.state.info(`Opened ${path}`);
        } catch (err) {
            this.state.error(`Could not open ${path}: ${err.message}`);
        }
    }

    /** Loads a scene from text the user supplied. */
    loadSceneFromText(text, label = 'scene') {
        try {
            this._adopt(deserialize(text, { flush: false, onWarning: (m) => this.state.warn(m) }));
            this.history.clear();
            this.state.currentScenePath = label;
            this.state.markClean();
            this.state.info(`Opened ${label}`);
        } catch (err) {
            this.state.error(`${label} is not a valid scene: ${err.message}`);
        }
    }

    /** Opens the file picker for a `.scene` file. */
    openSceneFromDisk() {
        const input = el('input', {
            type: 'file',
            accept: '.scene,.json,application/json',
            onchange: async () => {
                const file = input.files?.[0];
                if (!file) return;
                this.loadSceneFromText(await file.text(), file.name);
            },
        });
        input.click();
    }

    /**
     * Points the editor at a project folder.
     *
     * Uses the File System Access API where it exists, which is Chromium only;
     * elsewhere it falls back to asking for a URL, because a browser cannot read
     * a directory it was not handed.
     */
    async openProjectFromDisk() {
        if (!('showDirectoryPicker' in window)) {
            const url = prompt(
                'This browser cannot open a folder directly.\n\n'
                + 'Enter the URL the project is served from '
                + '(for example http://localhost:8080/Templates/2D%20Platformer/):',
                this.state.projectRoot);
            if (url) this._setProjectRoot(url);
            return;
        }

        try {
            this._directoryHandle = await window.showDirectoryPicker({ mode: 'readwrite' });
            this.state.projectName = this._directoryHandle.name;
            this.state.info(
                `Opened folder "${this._directoryHandle.name}". `
                + 'Files are read through the picker; assets still load over HTTP.');
            await this._loadProjectFromHandle();
        } catch (err) {
            if (err?.name !== 'AbortError') this.state.error(err.message);
        }
    }

    async _loadProjectFromHandle() {
        const handle = this._directoryHandle;
        if (!handle) return;

        try {
            const settingsFile = await (await handle.getFileHandle('ProjectSettings.json')).getFile();
            const config = EngineConfig.fromProjectSettings(await settingsFile.text());
            this.state.projectName = config.windowTitle;
            this.state.info(`Project: ${config.windowTitle}, start scene ${config.startScene}`);

            const scenes = await handle.getDirectoryHandle('Scenes').catch(() => null);
            if (!scenes) return;

            for await (const [name, entry] of scenes.entries()) {
                if (!name.endsWith('.scene')) continue;
                this.loadSceneFromText(await (await entry.getFile()).text(), `Scenes/${name}`);
                break;   // open the first; the assets panel lists the rest
            }
        } catch (err) {
            this.state.warn(`Folder opened, but no readable project inside: ${err.message}`);
        }
    }

    _setProjectRoot(url) {
        this.state.projectRoot = url.endsWith('/') ? url : `${url}/`;
        this.engine.assets.root = this.state.projectRoot;
        this.state.projectOpened.broadcast(this.state.projectRoot);
        this.state.info(`Project root set to ${this.state.projectRoot}`);
    }

    /**
     * Writes the scene out.
     *
     * Saves back to the opened folder when the File System Access API gave us a
     * writable handle; otherwise downloads the file, which is the only way a page
     * can put bytes on disk.
     */
    /**
     * Writes a file into the open project.
     *
     * Straight to disk when the project was opened through a folder picker, and a
     * download otherwise -- the same bargain saveScene makes, and the reason a
     * browser editor can write a project at all.
     */
    async writeProjectFile(path, text) {
        const parts = path.split('/').filter(Boolean);
        const name = parts.pop();

        if (this._directoryHandle) {
            try {
                let dir = this._directoryHandle;
                for (const segment of parts) dir = await dir.getDirectoryHandle(segment, { create: true });

                const file = await dir.getFileHandle(name, { create: true });
                const writable = await file.createWritable();
                await writable.write(text);
                await writable.close();
                return path;
            } catch (err) {
                this.state.warn(`Could not write ${path} (${err.message}); downloading instead.`);
            }
        }

        const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
        const link = el('a', { href: url, download: name });
        link.click();
        URL.revokeObjectURL(url);
        return path;
    }

    async saveScene() {
        const scene = this.scene;
        if (!scene) return;

        // Never save the play-time copy: it holds spawned controllers and pawns.
        const target = this.state.isPlaying ? deserialize(this._sceneSnapshot) : scene;
        const text = serialize(target);
        const name = (this.state.currentScenePath?.split('/').pop()) ?? `${scene.name}.scene`;

        if (this._directoryHandle) {
            try {
                const scenes = await this._directoryHandle.getDirectoryHandle('Scenes', { create: true });
                const file = await scenes.getFileHandle(name, { create: true });
                const writable = await file.createWritable();
                await writable.write(text);
                await writable.close();

                this.state.markClean();
                this.state.info(`Saved Scenes/${name}`);
                return;
            } catch (err) {
                this.state.warn(`Could not write to the folder (${err.message}); downloading instead.`);
            }
        }

        const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
        const link = el('a', { href: url, download: name });
        link.click();
        URL.revokeObjectURL(url);

        this.state.markClean();
        this.state.info(`Saved ${name}`);
    }

    _adopt(scene) {
        this.state.selectActor(null);
        this.engine.sceneManager.adoptScene(scene);
        this.state.notifyHierarchy();
        this.assets.refresh();
    }

    /** A history snapshot deliberately preserves only editor-owned scene state. */
    captureHistorySnapshot() {
        return { scene: serialize(this.scene), path: this.state.currentScenePath };
    }

    /** Restores a snapshot without clearing the undo stack that asked for it. */
    restoreHistorySnapshot(snapshot) {
        this._adopt(deserialize(snapshot.scene, { flush: false, onWarning: (m) => this.state.warn(m) }));
        this.state.currentScenePath = snapshot.path;
        this.state.markDirty();
        this.inspector.render();
    }

    // -------------------------------------------------------------------------
    // Play mode
    // -------------------------------------------------------------------------

    togglePlay() { return this.state.isPlaying ? this.stop() : this.play(); }

    /**
     * Starts play mode on a fresh copy of the scene.
     *
     * The copy is what makes stopping clean: whatever the game spawns, moves or
     * destroys happens to the copy, and the snapshot restores the scene exactly
     * as it was authored.
     */
    play() {
        const scene = this.scene;
        if (!scene || this.state.isPlaying) return;

        this._sceneSnapshot = serialize(scene);

        PlayMode.isActive = true;
        this.state.isPlaying = true;
        this.state.isPaused = false;

        this._adopt(deserialize(this._sceneSnapshot, { flush: false, onWarning: () => {} }));
        this.engine.simulate = true;
        this.engine.input.preventDefaults = true;

        this._playButton.textContent = '▶';
        this._playButton.classList.add('is-on');
        this.state.playStateChanged.broadcast(this.state);
        this.state.info('Playing.');
    }

    /** Leaves play mode and restores the authored scene. */
    stop() {
        if (!this.state.isPlaying) return;

        PlayMode.isActive = false;
        this.state.isPlaying = false;
        this.state.isPaused = false;
        Camera3D.playerView = null;

        this.engine.simulate = false;
        this.engine.input.preventDefaults = false;
        SB.Time.timeScale = 1;

        if (this._sceneSnapshot) {
            this._adopt(deserialize(this._sceneSnapshot, { flush: false, onWarning: () => {} }));
            this._sceneSnapshot = null;
        }

        this._playButton.textContent = '▶';
        this._playButton.classList.remove('is-on');
        this.state.playStateChanged.broadcast(this.state);
        this.state.info('Stopped.');
    }

    /** Freezes the simulation without leaving play mode. */
    togglePause() {
        if (!this.state.isPlaying) return;
        this.state.isPaused = !this.state.isPaused;
        // Time scale rather than stopping the loop, so the view still renders
        // and the editor stays responsive.
        SB.Time.timeScale = this.state.isPaused ? 0 : 1;
        this.state.playStateChanged.broadcast(this.state);
    }

    /** Advances exactly one frame while paused. */
    stepFrame() {
        if (!this.state.isPlaying || !this.state.isPaused) return;
        SB.Time.timeScale = 1;
        this.engine.tick(1 / 60, false);
        SB.Time.timeScale = 0;
        this.state.notifyHierarchy();
    }

    // -------------------------------------------------------------------------
    // Actor actions
    // -------------------------------------------------------------------------

    /** Adds a preset actor in front of the editor camera and selects it. */
    placeActor(preset) {
        if (!this.scene) return null;

        return this.history.snapshot(`Add ${preset.name}`, () => this._placeActor(preset));
    }

    _placeActor(preset) {
        const scene = this.scene;
        const actor = preset.build();

        // Drop it where the camera is looking, not at the origin, so it lands in
        // view rather than somewhere off screen.
        const t3d = actor.getComponent(Transform3D);
        if (t3d && this.viewport.camera) {
            const cameraTransform = this.viewport.camera.getTransform3D();
            const drop = Vector3.add(
                cameraTransform.position,
                Vector3.scale(cameraTransform.forward, Math.min(this.viewport._orbitDistance, 12)));
            t3d.localPosition = new Vector3(round(drop.x), Math.max(0, round(drop.y)), round(drop.z));
        }

        scene.addActor(actor, this.state.selectedLayer?.name ?? 'default');
        scene.flushPendingActors();

        this.state.markDirty();
        this.state.notifyHierarchy();
        this.state.selectActor(actor);
        this.state.info(`Added ${preset.name}.`);
        return actor;
    }

    /** Copies an actor through the serialiser, so components come with it. */
    duplicateActor(actor) {
        if (!this.scene || !actor) return null;

        return this.history.snapshot(`Duplicate ${actor.name}`, () => this._duplicateActor(actor));
    }

    _duplicateActor(actor) {
        const scene = this.scene;
        const copy = buildActor(buildActorDto(actor), (m) => this.state.warn(m));
        copy.name = `${actor.name} copy`;

        // The DTO nests children, so `copy` may be a whole subtree; addActor takes all of it.
        scene.addActor(copy, actor.layerRef?.name ?? 'default');
        scene.flushPendingActors();

        this.state.markDirty();
        this.state.notifyHierarchy();
        this.state.selectActor(copy);
        return copy;
    }

    deleteActor(actor) {
        if (!actor) return;
        this.history.snapshot(`Delete ${actor.name}`, () => {
            if (this.state.selectedActor === actor) this.state.selectActor(null);
            actor.destroy();
            this.scene?.flushPendingActors();
            this.state.markDirty();
            this.state.notifyHierarchy();
        });
    }

    renameActor(actor) {
        const name = prompt('Actor name', actor.name);
        if (name == null) return;
        this.history.snapshot(`Rename ${actor.name}`, () => {
            actor.name = name;
            this.state.markDirty();
            this.state.notifyHierarchy();
            this.inspector.render();
        });
    }

    /**
     * Attaches one actor to another, so it moves with it and is destroyed with it.
     * A cycle is refused by the engine; the editor reports it rather than throwing.
     */
    attachActor(child, parent) {
        if (!child) return;
        this.history.snapshot(`Attach ${child.name}`, () => {
            try {
                child.attachTo(parent ?? null);
            } catch (err) {
                this.state.warn(err.message);
                return;
            }
            // Attachment can move a child between layers when the two differ; the scene keeps
            // the subtree together, so the outliner has to be rebuilt from scratch.
            this.scene?.flushPendingActors();
            this.state.markDirty();
            this.state.notifyHierarchy();
        });
    }

    /** Detaches an actor from its parent, or (with `children`) detaches its children. */
    detachActor(actor, children = false) {
        if (!actor) return;
        this.history.snapshot(children ? `Detach children of ${actor.name}` : `Detach ${actor.name}`, () => {
            if (children) actor.detachChildren();
            else actor.detach();
            this.state.markDirty();
            this.state.notifyHierarchy();
        });
    }

    moveActorToLayer(actor, layerName) {
        if (!actor) return;
        this.history.snapshot(`Move ${actor.name} to ${layerName}`, () => {
            this.scene?.moveActor(actor, layerName);
            this.scene?.flushPendingActors();
            this.state.markDirty();
            this.state.notifyHierarchy();
        });
    }

    focusOnActor(actor) { this.viewport.focusOn(actor); }

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------

    _installShortcuts() {
        window.addEventListener('keydown', (e) => {
            const meta = e.ctrlKey || e.metaKey;
            if (meta && e.key.toLowerCase() === 'p') {
                e.preventDefault();
                this.toggleCommandPalette();
                return;
            }
            if (this._commandPalette?.root.classList.contains('is-open')) {
                if (e.key === 'Escape') this.toggleCommandPalette(false);
                return;
            }

            // Never steal a key from a field the user is typing in.
            const tag = document.activeElement?.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') {
                if (e.key === 'Escape') document.activeElement.blur();
                return;
            }

            if (e.key === 'F5') { e.preventDefault(); this.executeCommand('editor.play.toggle'); }
            else if (e.key === 'F6') { e.preventDefault(); this.togglePause(); }
            else if (e.key === 'F7') { e.preventDefault(); this.stop(); }
            else if (e.key === 'F8') { e.preventDefault(); this.stepFrame(); }
            else if (meta && e.key.toLowerCase() === 's') { e.preventDefault(); this.executeCommand('editor.save'); }
            else if (meta && e.key.toLowerCase() === 'z') {
                e.preventDefault();
                this.executeCommand(e.shiftKey ? 'editor.redo' : 'editor.undo');
            } else if (meta && e.key.toLowerCase() === 'y') {
                e.preventDefault(); this.executeCommand('editor.redo');
            } else if (e.key.toLowerCase() === 'w') {
                this.executeCommand('editor.transform.translate');
            } else if (e.key.toLowerCase() === 'e') {
                this.executeCommand('editor.transform.rotate');
            } else if (e.key.toLowerCase() === 'r') {
                this.executeCommand('editor.transform.scale');
            } else if (meta && e.key.toLowerCase() === 'd' && this.state.selectedActor) {
                e.preventDefault();
                this.executeCommand('editor.duplicate');
            } else if ((e.key === 'Delete' || e.key === 'Backspace') && this.state.selectedActor) {
                e.preventDefault();
                this.executeCommand('editor.delete');
            } else if (e.key === 'f' && this.state.selectedActor) {
                this.executeCommand('editor.focus');
            } else if (e.key === 'Escape') {
                this.state.selectActor(null);
            }
        });
    }

    /** Lets a `.scene` file be dropped straight onto the viewport. */
    _installDropTarget() {
        const surface = this.viewport.root;

        surface.addEventListener('dragover', (e) => {
            e.preventDefault();
            surface.classList.add('is-drop-target');
        });

        surface.addEventListener('dragleave', () => surface.classList.remove('is-drop-target'));

        surface.addEventListener('drop', async (e) => {
            e.preventDefault();
            surface.classList.remove('is-drop-target');

            const file = e.dataTransfer?.files?.[0];
            if (!file) return;

            if (!/\.(scene|json)$/i.test(file.name)) {
                this.state.warn(`${file.name} is not a scene file.`);
                return;
            }
            this.loadSceneFromText(await file.text(), file.name);
        });
    }
}

function round(value) { return Math.round(value * 100) / 100; }

/** Boots the editor into the page. */
export async function boot(options = {}) {
    const editor = new Editor(options);
    await editor.start();
    globalThis.sexybiscuitEditor = editor;
    return editor;
}
