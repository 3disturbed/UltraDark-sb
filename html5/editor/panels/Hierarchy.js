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
            for (const actor of actors) this.root.append(this._actorRow(actor, layer));
        }

        this._highlight();
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

    _actorRow(actor, layer) {
        // A button, not a div: the row is a click target, and a div is not
        // reachable by keyboard or announced as actionable.
        const row = el('button.sb-actor-row', {
            type: 'button',
            dataset: { actorId: String(actor.id) },
            title: `${actor.name}  (${actor.tag})`,
            onclick: () => this.state.selectActor(actor),
            ondblclick: () => this.editor.focusOnActor(actor),
        },
            el('span.sb-actor-icon', { text: iconFor(actor) }),
            el('span.sb-actor-name', { text: truncate(actor.name, 28) }),
            actor.tag !== 'Untagged' ? el('span.sb-actor-tag', { text: actor.tag }) : null,
            el('button.sb-row-action', {
                type: 'button',
                text: '⋯',
                title: 'Actions',
                onclick: (e) => { e.stopPropagation(); this._menu(actor, layer, e); },
            }));

        if (!actor.isActive) row.classList.add('is-inactive');
        return row;
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
