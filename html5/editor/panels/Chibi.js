// -----------------------------------------------------------------------------
// MakeChibi — the character panel.
//
// It edits the selected character rather than a preview of one: select an actor
// with a ChibiCharacter and every slider rebuilds that actor in the viewport, so
// what you are looking at is the thing you will ship. With nothing selected the
// panel holds a recipe you are authoring, and "Place in scene" turns it into an
// actor that then stays linked.
//
// Slot lists come from the part table's own keys, so a hairstyle added to
// chibi-parts.json appears here without a panel edit.
// -----------------------------------------------------------------------------

import { el, clear, field, formatNumber } from '../dom.js';
import {
    SLOTS, COLOUR_SLOTS, ChibiParts, variantsFor, accessoryNames,
    defaultRecipe, normaliseRecipe, stringifyRecipe, randomRecipe, PROPORTIONS,
    ChibiCharacter, ChibiAnimator, clipNames, buildChibi, Actor, Transform3D,
} from '../../src/index.js';

/** Rebuilding forty actors on every drag of a slider is wasted work. */
const REBUILD_DELAY = 90;

/** Where a saved recipe goes. Under Assets/, because that is what a build copies. */
const RECIPE_DIR = 'Assets/Characters';

const PROPORTION_LABELS = {
    height: 'Height', headSize: 'Head', bodyWidth: 'Body',
    limbThickness: 'Limbs', legLength: 'Legs', armLength: 'Arms',
};

const TABS = ['Body', 'Style', 'Colour', 'Wardrobe', 'Motion'];

export class ChibiPanel {
    constructor(state, editor) {
        this.state = state;
        this.editor = editor;
        this.root = el('div.sb-panel-body.sb-chibi');

        this.recipe = defaultRecipe();
        this.tab = 'Body';
        /** @type {?ChibiCharacter} The character every edit is applied to, if any. */
        this.linked = null;
        this._rebuildTimer = null;
        this._savedPath = null;
        // Edits since the recipe last matched something a scene can name -- a file
        // on disk, or the seed it came from. See _placeInScene.
        this._unsaved = false;

        state.selectionChanged.add(() => this._adoptSelection());
        this.render();
    }

    // ---- Linking to the selection -------------------------------------------

    _adoptSelection() {
        const actor = this.state.selectedActor;
        const character = actor?.getComponent?.(ChibiCharacter) ?? null;

        if (!character) {
            // Keep editing the loose recipe rather than resetting it: clicking a
            // light should not lose the character you were half-way through.
            if (this.linked) { this.linked = null; this.render(); }
            return;
        }

        this.linked = character;
        this.recipe = normaliseRecipe(character.chibi?.recipe ?? character.localRecipe());
        this._savedPath = character.recipePath || null;
        this._unsaved = false;
        this.render();
    }

    // ---- Applying an edit ----------------------------------------------------

    /** Rebuilds the linked character, coalescing the flood a dragged slider makes. */
    _apply({ immediate = false } = {}) {
        this.state.markDirty();
        this._unsaved = true;
        if (!this.linked) return;

        clearTimeout(this._rebuildTimer);
        const run = () => {
            this.linked.rebuild(this.recipe);
            this.linked.actor?.scene?.flushPendingActors();
            // The animator cached transforms that have just been destroyed.
            this.linked.actor?.getComponent(ChibiAnimator)?.resolve();
            this.state.notifyHierarchy();
        };
        if (immediate) run(); else this._rebuildTimer = setTimeout(run, REBUILD_DELAY);
    }

    // ---- Actions -------------------------------------------------------------

    _randomise() {
        const seed = 1 + Math.floor(Math.random() * 1_000_000);
        this.recipe = randomRecipe(seed);
        if (this.linked) this.linked.seed = seed;
        this._savedPath = null;
        this._apply({ immediate: true });
        // A seeded character needs no file: the seed rebuilds it exactly.
        this._unsaved = false;
        this.render();
    }

    _fresh() {
        this.recipe = defaultRecipe();
        this._savedPath = null;
        this._apply({ immediate: true });
        this._unsaved = false;      // the default character needs no file either
        this.render();
    }

    /**
     * Adds a character to the scene and links the panel to it.
     *
     * A scene stores a path or a seed, not a recipe, so a hand-edited character
     * that has never been written to disk would come back as the default one the
     * next time the scene loaded. Saving first is the only way that is not a
     * surprise; a character straight from Randomise needs nothing, because its
     * seed rebuilds it exactly.
     */
    async _placeInScene() {
        if (this._unsaved && !this._savedPath) {
            this.state.info('Saving the recipe first, so the scene can find it again.');
            if (!await this._save()) return;
        }

        const actor = this.editor.placeActor({
            name: this.recipe.name || 'Chibi',
            build: () => {
                const created = new Actor(this.recipe.name || 'Chibi');
                created.addComponent(Transform3D);

                const character = created.addComponent(ChibiCharacter);
                character.recipePath = this._savedPath ?? '';
                character.seed = this.recipe.seed ?? 0;
                // The body is built below from the recipe in hand, which may not
                // have been saved yet; letting Start read the file would either
                // find nothing or find an older version.
                character.buildOnStart = false;

                created.addComponent(ChibiAnimator);
                return created;
            },
        });
        if (!actor) return;

        const character = actor.getComponent(ChibiCharacter);
        character.rebuild(this.recipe);
        actor.scene?.flushPendingActors();
        actor.getComponent(ChibiAnimator)?.resolve();

        this.linked = character;
        this.state.notifyHierarchy();
        this.render();
    }

    /**
     * Expands the character into plain actors.
     *
     * The component is the right default -- a scene stays three lines instead of
     * forty -- but a character that needs one arm moved by hand needs the arm to
     * be in the file, and this is the way out.
     */
    _bake() {
        const scene = this.editor.scene;
        if (!scene) { this.state.warn('Open a scene first.'); return; }

        const built = buildChibi(this.recipe, (m) => this.state.warn(m));
        scene.addActor(built.actor, this.state.selectedLayer?.name ?? 'default');
        scene.flushPendingActors();

        this.state.markDirty();
        this.state.notifyHierarchy();
        this.state.selectActor(built.actor);
        this.state.info(`Baked ${built.parts.length} parts into the scene.`);
    }

    async _save() {
        const name = (this.recipe.name || 'Chibi').replace(/[^\w -]/g, '').trim() || 'Chibi';
        const path = `${RECIPE_DIR}/${name}.chibi`;
        const written = await this.editor.writeProjectFile(path, stringifyRecipe(this.recipe));
        if (!written) return null;

        this._savedPath = path;
        this._unsaved = false;
        if (this.linked) this.linked.recipePath = path;
        this.state.info(`Saved ${path}`);
        this.render();
        return path;
    }

    // ---- Rendering -----------------------------------------------------------

    render() {
        clear(this.root);
        this.root.append(this._header(), this._tabs(), this._body());
    }

    _header() {
        const name = el('input.sb-text.sb-chibi-name', {
            type: 'text',
            value: this.recipe.name ?? 'Chibi',
            oninput: () => { this.recipe.name = name.value; this.state.markDirty(); },
        });

        const link = this.linked
            ? el('span.sb-chibi-link', { text: `editing ${this.linked.actor?.name ?? 'a character'}` })
            : el('span.sb-chibi-link.is-loose', { text: 'no character selected' });

        return el('div.sb-chibi-header', {},
            name,
            link,
            el('div.sb-chibi-actions', {},
                this._button('Randomise', 'A whole new coordinated character', () => this._randomise()),
                this._button('New', 'Back to the default character', () => this._fresh()),
                this._button('Place', 'Add this character to the scene', () => this._placeInScene()),
                this._button('Save', `Write ${RECIPE_DIR}/<name>.chibi`, () => this._save()),
                this._button('Bake', 'Expand into plain actors you can hand-edit',
                    () => this._bake())));
    }

    _button(label, title, onclick) {
        return el('button.sb-chibi-button', { type: 'button', text: label, title, onclick });
    }

    _tabs() {
        const row = el('div.sb-chibi-tabs');
        for (const name of TABS) {
            row.append(el(`button.sb-chibi-tab${name === this.tab ? '.is-active' : ''}`, {
                type: 'button',
                text: name,
                onclick: () => { this.tab = name; this.render(); },
            }));
        }
        return row;
    }

    _body() {
        const body = el('div.sb-chibi-body');
        switch (this.tab) {
            case 'Body': body.append(...this._bodyFields()); break;
            case 'Style': body.append(...this._styleFields()); break;
            case 'Colour': body.append(...this._colourFields()); break;
            case 'Wardrobe': body.append(...this._wardrobeFields()); break;
            case 'Motion': body.append(...this._motionFields()); break;
            default: break;
        }
        return body;
    }

    _bodyFields() {
        return PROPORTIONS.map((key) => {
            const readout = el('span.sb-chibi-readout', {
                text: formatNumber(this.recipe.proportions[key]),
            });
            const slider = el('input.sb-chibi-slider', {
                type: 'range', min: 0.5, max: 2, step: 0.01,
                value: this.recipe.proportions[key],
                oninput: () => {
                    this.recipe.proportions[key] = Number(slider.value);
                    readout.textContent = formatNumber(this.recipe.proportions[key]);
                    this._apply();
                },
            });
            return field(PROPORTION_LABELS[key] ?? key, slider, readout);
        });
    }

    _styleFields() {
        return SLOTS.map((slot) => {
            const select = el('select.sb-chibi-select', {
                onchange: () => { this.recipe.style[slot] = select.value; this._apply({ immediate: true }); },
            });
            for (const variant of variantsFor(slot)) {
                select.append(el('option', {
                    value: variant, text: variant, selected: this.recipe.style[slot] === variant,
                }));
            }
            return field(slot[0].toUpperCase() + slot.slice(1), select);
        });
    }

    _colourFields() {
        return COLOUR_SLOTS.map((slot) => {
            const picker = el('input.sb-chibi-colour', {
                type: 'color',
                value: toHexRgb(this.recipe.colours[slot]),
                oninput: () => {
                    this.recipe.colours[slot] = picker.value.toUpperCase();
                    // Recolouring needs no rebuild: the renderers are already there.
                    if (this.linked?.chibi) this.linked.chibi.setColour(slot, picker.value);
                    this.state.markDirty();
                },
            });

            const swatches = el('div.sb-chibi-swatches');
            for (const hex of paletteFor(slot)) {
                swatches.append(el('button.sb-chibi-swatch', {
                    type: 'button', title: hex, style: { background: hex },
                    onclick: () => {
                        this.recipe.colours[slot] = hex;
                        picker.value = toHexRgb(hex);
                        if (this.linked?.chibi) this.linked.chibi.setColour(slot, hex);
                        this.state.markDirty();
                    },
                }));
            }

            return field(slot[0].toUpperCase() + slot.slice(1), picker, swatches);
        });
    }

    _wardrobeFields() {
        const rows = [];
        this.recipe.accessories.forEach((entry, index) => {
            const select = el('select.sb-chibi-select', {
                onchange: () => { entry.part = select.value; this._apply({ immediate: true }); },
            });
            for (const part of accessoryNames()) {
                select.append(el('option', { value: part, text: part, selected: entry.part === part }));
            }

            const colour = el('input.sb-chibi-colour', {
                type: 'color',
                value: toHexRgb(entry.colour ?? this.recipe.colours.accent),
                oninput: () => { entry.colour = colour.value.toUpperCase(); this._apply({ immediate: true }); },
            });

            rows.push(field(`Slot ${index + 1}`, select, colour,
                this._button('Remove', 'Take this off', () => {
                    this.recipe.accessories.splice(index, 1);
                    this._apply({ immediate: true });
                    this.render();
                })));
        });

        rows.push(this._button('Add accessory', 'Hang something off a socket', () => {
            this.recipe.accessories.push({ part: accessoryNames()[0], colour: null });
            this._apply({ immediate: true });
            this.render();
        }));
        return rows;
    }

    _motionFields() {
        const animator = this.linked?.actor?.getComponent(ChibiAnimator) ?? null;
        if (!animator) {
            return [el('p.sb-chibi-note', {
                text: 'Select a character with a ChibiAnimator to preview its motion.',
            })];
        }

        const clips = el('div.sb-chibi-clips');
        for (const name of clipNames()) {
            clips.append(el(`button.sb-palette-item${animator.clip === name ? '.is-active' : ''}`, {
                type: 'button', text: name,
                onclick: () => { animator.play(name); this.render(); },
            }));
        }

        const speed = el('input.sb-chibi-slider', {
            type: 'range', min: 0.1, max: 3, step: 0.05, value: animator.speed,
            oninput: () => { animator.speed = Number(speed.value); },
        });

        return [
            field('Speed', speed),
            el('p.sb-chibi-note', { text: 'Locomotion scales with intensity; keyed clips do not.' }),
            clips,
        ];
    }
}

/** A colour input only accepts `#RRGGBB`, so trim any alpha off first. */
function toHexRgb(value) {
    const hex = String(value ?? '#FFFFFF').replace('#', '');
    return `#${hex.slice(0, 6).padEnd(6, 'F')}`;
}

/** The curated palette a colour slot draws from, flattening the outfit pairs. */
function paletteFor(slot) {
    if (slot === 'top') return ChibiParts.palettes.outfit.map((pair) => pair[0]);
    if (slot === 'bottom') return ChibiParts.palettes.outfit.map((pair) => pair[1]);
    return ChibiParts.palettes[slot] ?? [];
}
