// -----------------------------------------------------------------------------
// Hierarchy — the scene tree: layers, then the actors in each.
// -----------------------------------------------------------------------------

import { el, clear, truncate } from '../dom.js';

/** The world outliner. */
export class HierarchyPanel {
    /**
     * @param {import('../EditorState.js').EditorState} state
     * @param {object} editor The editor app, for the actions the context menu needs.
     */
    constructor(state, editor) {
        this.state = state;
        this.editor = editor;
        this.root = el('div.sb-panel-body.sb-hierarchy');
        this._filter = '';
        this._openMenu = null;
        this._dismissMenu = null;

        // The actor a drag started on. Chrome will not let dragover read the payload, so
        // the drop target has nothing else to check "would this make a cycle?" against.
        this._dragCandidate = null;

        state.hierarchyChanged.add(() => this.render());
        state.selectionChanged.add(() => this._highlight());
    }

    /** Rebuilds the whole tree. */
    render() {
        const scene = this.editor.scene;
        clear(this.root);

        if (!scene) {
            this.root.append(el('div.sb-empty', { text: 'No scene open.' }));
            return;
        }

        this.root.append(el('input.sb-search', {
            type: 'search',
            placeholder: 'Filter actors…',
            value: this._filter,
            oninput: (e) => { this._filter = e.target.value.toLowerCase(); this.render(); },
        }));

        for (const layer of scene.layers) {
            const actors = this._filter
                ? layer.actors.filter((a) => a.name.toLowerCase().includes(this._filter))
                : layer.actors;

            // A filter that matches nothing in a layer hides the layer too, so
            // the result reads as a list rather than a tree full of empty branches.
            if (this._filter && actors.length === 0) continue;

            this.root.append(this._layerRow(layer, actors));

            // While filtering, matches are listed flat: a match whose parent does not
            // match would otherwise be hidden inside a branch that was filtered away.
            if (this._filter) {
                for (const actor of actors) this.root.append(this._actorRow(actor, layer, 0));
                continue;
            }

            for (const actor of actors) {
                if (actor.parent) continue;   // drawn under its parent instead
                this._appendSubtree(actor, layer, 0);
            }
        }

        this._highlight();
    }

    /** Appends an actor and, indented beneath it, everything attached to it. */
    _appendSubtree(actor, layer, depth) {
        this.root.append(this._actorRow(actor, layer, depth));
        for (const child of actor.children) this._appendSubtree(child, layer, depth + 1);
    }

    _layerRow(layer, actors) {
        return el('div.sb-layer-row', {
            onclick: () => { this.state.selectedLayer = layer; },
        },
            el('button.sb-eye', {
                type: 'button',
                title: layer.visible ? 'Hide this layer' : 'Show this layer',
                text: layer.visible ? '◉' : '○',
                onclick: (e) => {
                    e.stopPropagation();
                    layer.visible = !layer.visible;
                    layer.active = layer.visible;
                    this.render();
                },
            }),
            el('span.sb-layer-name', { text: layer.name }),
            el('span.sb-layer-count', { text: String(actors.length) }));
    }

    _actorRow(actor, layer, depth = 0) {
        // A button, not a div: the row is a click target, and a div is not
        // reachable by keyboard or announced as actionable.
        const row = el('button.sb-actor-row', {
            type: 'button',
            draggable: true,
            dataset: { actorId: String(actor.id) },
            title: `${actor.hierarchyPath}  (${actor.tag})`,
            style: depth > 0 ? { paddingLeft: `${8 + depth * 14}px` } : null,
            onclick: () => this.state.selectActor(actor),
            ondblclick: () => this.editor.focusOnActor(actor),

            // Drag an actor onto another to attach it. The id rides in the drag data
            // rather than in a field on `this`, so a drag that starts in this panel and
            // ends somewhere else cannot leave a stale actor behind.
            ondragstart: (e) => {
                e.dataTransfer.setData('application/x-sb-actor', String(actor.id));
                e.dataTransfer.effectAllowed = 'move';
                this._dragCandidate = actor;
            },
            ondragend: () => { this._dragCandidate = null; },
            ondragover: (e) => {
                const dragged = this._draggedFrom(e);
                if (!dragged || dragged === actor || actor.isDescendantOf(dragged)) return;
                e.preventDefault();                       // "yes, you may drop here"
                e.dataTransfer.dropEffect = 'move';
                row.classList.add('is-drop-target');
            },
            ondragleave: () => row.classList.remove('is-drop-target'),
            ondrop: (e) => {
                row.classList.remove('is-drop-target');
                const dragged = this._draggedFrom(e);
                if (!dragged || dragged === actor) return;
                e.preventDefault();
                this.editor.attachActor(dragged, actor);
            },
        },
            depth > 0 ? el('span.sb-actor-branch', { text: '└' }) : null,
            el('span.sb-actor-icon', { text: iconFor(actor) }),
            el('span.sb-actor-name', { text: truncate(actor.name, 28) }),
            actor.tag !== 'Untagged' ? el('span.sb-actor-tag', { text: actor.tag }) : null,
            el('button.sb-row-action', {
                type: 'button',
                text: '⋯',
                title: 'Actions',
                onclick: (e) => { e.stopPropagation(); this._menu(actor, layer, e); },
            }));

        // Greyed out when an ancestor is off too: a child of a disabled parent is not
        // running either, and the outliner should not claim otherwise.
        if (!actor.isActiveInHierarchy) row.classList.add('is-inactive');
        return row;
    }

    /** The actor a drag event is carrying, or null when it is carrying something else. */
    _draggedFrom(event) {
        const raw = event.dataTransfer?.getData('application/x-sb-actor');
        if (!raw) {
            // Chrome hides the data during dragover for security; the types list is all
            // that is readable there, so fall back to the actor recorded at dragstart.
            return event.dataTransfer?.types?.includes('application/x-sb-actor')
                ? this._dragCandidate ?? null
                : null;
        }
        return this._byId(Number(raw));
    }

    _byId(id) {
        for (const layer of this.editor.scene?.layers ?? []) {
            for (const actor of layer.actors) if (actor.id === id) return actor;
        }
        return null;
    }

    _menu(actor, layer, event) {
        this.state.selectActor(actor);
        this._closeMenu();

        const menu = el('div.sb-context-menu', {
            style: { left: `${event.clientX}px`, top: `${event.clientY}px` },
        },
            this._menuItem('Rename', () => this.editor.renameActor(actor)),
            this._menuItem('Duplicate', () => this.editor.duplicateActor(actor)),
            this._menuItem(actor.isActive ? 'Deactivate' : 'Activate', () => {
                actor.isActive = !actor.isActive;
                this.state.markDirty();
                this.render();
            }),
            actor.parent
                ? this._menuItem(`Detach from ${actor.parent.name}`, () => this.editor.detachActor(actor))
                : null,
            actor.children.length > 0
                ? this._menuItem('Detach children', () => this.editor.detachActor(actor, true))
                : null,
            el('hr'),
            ...this.editor.scene.layers
                .filter((l) => l !== layer)
                .map((l) => this._menuItem(`Move to ${l.name}`, () => {
                    this.editor.moveActorToLayer(actor, l.name);
                })),
            el('hr'),
            this._menuItem('Delete', () => this.editor.deleteActor(actor), 'is-danger'));

        document.body.append(menu);
        this._openMenu = menu;

        // A press outside the menu dismisses it. The test for "outside" is
        // essential: this listener runs on pointerdown, which precedes click, so
        // dismissing unconditionally tore the menu out of the DOM before the item
        // the user pressed could dispatch its click — every menu action silently
        // did nothing. An item closes the menu itself, once it has run.
        const onPointerDown = (event) => {
            if (menu.contains(event.target)) return;
            this._closeMenu();
        };

        this._dismissMenu = () => document.removeEventListener('pointerdown', onPointerDown, true);
        document.addEventListener('pointerdown', onPointerDown, true);

        // Escape closes it too, and keyboard focus starts on the first item so
        // the menu is usable without a pointer at all.
        menu.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') this._closeMenu();
        });
        menu.querySelector('.sb-menu-item')?.focus();
    }

    _closeMenu() {
        this._dismissMenu?.();
        this._dismissMenu = null;
        this._openMenu?.remove();
        this._openMenu = null;
    }

    _menuItem(label, action, extraClass = '') {
        return el(`button.sb-menu-item${extraClass ? `.${extraClass}` : ''}`, {
            type: 'button',
            text: label,
            onclick: () => {
                // Close first: the action routinely rebuilds the hierarchy, and a
                // menu left over a redrawn tree points at rows that have gone.
                this._closeMenu();
                action();
            },
        });
    }

    _highlight() {
        const selectedId = this.state.selectedActor?.id;
        for (const row of this.root.querySelectorAll('.sb-actor-row')) {
            row.classList.toggle('is-selected', Number(row.dataset.actorId) === selectedId);
        }
    }
}

/** A glyph hinting at what an actor is, from the components it carries. */
function iconFor(actor) {
    const has = (name) => actor.getComponent(name) != null;

    if (has('Camera3D') || has('Camera2D')) return '◳';
    if (has('Light3D') || has('SkyLight')) return '☀';
    if (has('Skybox')) return '☁';
    if (has('PlayerStart')) return '⚑';
    if (has('MeshRenderer')) return '◆';
    if (has('SpriteRenderer')) return '▣';
    if (has('ScriptComponent')) return '⌨';
    if (actor.constructor.name !== 'Actor') return '★';
    return '·';
}
