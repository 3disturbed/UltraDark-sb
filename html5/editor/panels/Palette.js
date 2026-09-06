// -----------------------------------------------------------------------------
// Palette — the Place Actors list, and the scene/project controls beside it.
// -----------------------------------------------------------------------------

import { el, clear } from '../dom.js';
import { ActorPresets, presetCategories } from '../../src/scene/ActorPresets.js';

/** The searchable list of ready-made actors. */
export class PalettePanel {
    constructor(state, editor) {
        this.state = state;
        this.editor = editor;
        this.root = el('div.sb-panel-body.sb-palette');
        this._filter = '';
        this.render();
    }

    render() {
        clear(this.root);

        this.root.append(el('input.sb-search', {
            type: 'search',
            placeholder: 'Search actors…',
            value: this._filter,
            oninput: (e) => { this._filter = e.target.value.toLowerCase(); this.render(); },
        }));

        const matches = this._filter
            ? ActorPresets.filter((p) =>
                p.name.toLowerCase().includes(this._filter)
                || p.description.toLowerCase().includes(this._filter))
            : ActorPresets;

        if (matches.length === 0) {
            this.root.append(el('div.sb-empty', { text: 'Nothing matches that.' }));
            return;
        }

        for (const category of presetCategories()) {
            const inCategory = matches.filter((p) => p.category === category);
            if (inCategory.length === 0) continue;

            this.root.append(el('div.sb-palette-category', { text: category }));
            for (const preset of inCategory) {
                this.root.append(el('button.sb-palette-item', {
                    type: 'button',
                    title: preset.description,
                    onclick: () => this.editor.placeActor(preset),
                },
                    el('span.sb-palette-name', { text: preset.name }),
                    el('span.sb-palette-desc', { text: preset.description })));
            }
        }
    }
}
