// -----------------------------------------------------------------------------
// Inspector — edits the selected actor and its components.
//
// Fields are built from each component's declared schema, which is the same
// source the serialiser reads. That is deliberate: what the inspector can change
// is exactly what a scene file can hold, so nothing edited here is silently lost
// on save, and nothing saved is invisible here.
// -----------------------------------------------------------------------------

import { el, clear, field, numberInput, formatNumber } from '../dom.js';
import { schemaOf, listComponents, componentNameOf } from '../../src/core/TypeRegistry.js';
import { PropertyType } from '../../src/core/PropertyTypes.js';
import { Vector2, Vector3, Color, Quaternion } from '../../src/math/index.js';
import { Transform3D } from '../../src/core/Transform3D.js';
import { MissingComponent } from '../../src/core/MissingComponent.js';
import { appendListValue, formatListValue, parseListValue } from '../listValue.js';

/** The details panel. */
export class InspectorPanel {
    constructor(state, editor) {
        this.state = state;
        this.editor = editor;
        this._fieldTransaction = null;
        this.root = el('div.sb-panel-body.sb-inspector');

        state.selectionChanged.add(() => this.render());
    }

    render() {
        const actor = this.state.selectedActor;
        clear(this.root);

        if (!actor || actor.isDestroyed) {
            this.root.append(el('div.sb-empty', { text: 'Select an actor to edit it.' }));
            return;
        }

        this.root.append(this._actorSection(actor));
        this.root.append(this._transformSection(actor));

        const transform3D = actor.getComponent(Transform3D);
        if (transform3D) this.root.append(this._transform3DSection(transform3D));

        for (const component of actor.getAllComponents()) {
            if (component === actor.transform || component instanceof Transform3D) continue;
            this.root.append(this._componentSection(actor, component));
        }

        this.root.append(this._addComponentControl(actor));
    }

    // ---- Sections -----------------------------------------------------------

    _actorSection(actor) {
        return this._section('Actor', [
            field('Name', this._trackFieldEdit('Rename actor', el('input.sb-text', {
                type: 'text',
                value: actor.name,
                oninput: (e) => {
                    actor.name = e.target.value;
                    this.state.notifyHierarchy();
                },
            }))),
            field('Tag', this._trackFieldEdit('Set actor tag', el('input.sb-text', {
                type: 'text',
                value: actor.tag,
                oninput: (e) => { actor.tag = e.target.value; },
            }))),
            field('Layer', this._trackFieldEdit('Set actor layer', numberInput(actor.layer, (v) => {
                actor.layer = Math.round(v);
            }, { step: 1 }))),
            field('Active', this._checkbox(actor.isActive, (v) => {
                actor.isActive = v;
                this.state.notifyHierarchy();
            }, 'Set actor active state')),
            field('Life span', this._trackFieldEdit('Set actor life span', numberInput(actor.lifeSpan, (v) => {
                actor.lifeSpan = v;
            }, { step: 0.1, min: 0 }))),
        ], { id: `Actor #${actor.id}` });
    }

    _transformSection(actor) {
        const t = actor.transform;
        return this._section('Transform (2D)', [
            field('Position', ...this._vectorInputs(t.localPosition, (v) => {
                t.localPosition = v;
            }, ['x', 'y'], { historyLabel: 'Set 2D position' })),
            field('Rotation', this._trackFieldEdit('Set 2D rotation', numberInput(t.localRotation * 180 / Math.PI, (v) => {
                t.localRotation = v * Math.PI / 180;
            }, { step: 1 })), el('span.sb-unit', { text: '°' })),
            field('Scale', ...this._vectorInputs(t.localScale, (v) => {
                t.localScale = v;
            }, ['x', 'y'], { historyLabel: 'Set 2D scale' })),
        ]);
    }

    _transform3DSection(t) {
        // Euler angles are what a person can actually type; the quaternion is
        // what the file stores. The conversion round-trips exactly, unlike the
        // C# engine's — see Quaternion.toEuler.
        const euler = t.localEulerAngles;

        return this._section('Transform (3D)', [
            field('Position', ...this._vectorInputs(t.localPosition, (v) => {
                t.localPosition = v;
            }, ['x', 'y', 'z'], { historyLabel: 'Set 3D position' })),
            field('Rotation', ...this._vectorInputs(euler, (v) => {
                t.localEulerAngles = v;
            }, ['x', 'y', 'z'], { step: 1, historyLabel: 'Set 3D rotation' })),
            field('Scale', ...this._vectorInputs(t.localScale, (v) => {
                t.localScale = v;
            }, ['x', 'y', 'z'], { historyLabel: 'Set 3D scale' })),
        ]);
    }

    _componentSection(actor, component) {
        const name = componentNameOf(component);

        // A placeholder for a type this runtime cannot load — almost always
        // behaviour compiled into a C# assembly. Its data is intact and will be
        // written back untouched, which is worth saying rather than showing an
        // empty box.
        if (component instanceof MissingComponent) {
            return this._section(`${component.typeName}`, [
                el('p.sb-note', {
                    text: 'This component type is not available in the web runtime — '
                        + 'it is most likely defined in the project’s C# code. Its settings are '
                        + 'preserved exactly and will be written back on save.',
                }),
                el('pre.sb-json', { text: JSON.stringify(component.properties ?? {}, null, 2) }),
            ], { removable: false, missing: true });
        }

        const schema = schemaOf(component.constructor);
        const rows = [
            field('Enabled', this._checkbox(component.enabled, (v) => { component.enabled = v; },
                `Set ${name} enabled`)),
        ];

        for (const [key, descriptor] of Object.entries(schema)) {
            if (key === 'enabled' || descriptor.hidden || descriptor.transient) continue;
            rows.push(this._propertyField(component, key, descriptor));
        }

        return this._section(name, rows, {
            onRemove: () => {
                this.editor.history.snapshot(`Remove ${name}`, () => {
                    actor.removeComponent(component);
                    this.state.markDirty();
                    this.render();
                    this.state.notifyHierarchy();
                });
            },
        });
    }

    // ---- Fields -------------------------------------------------------------

    _propertyField(target, key, descriptor) {
        const label = humanise(key);
        const value = target[key];
        const commit = (next) => { target[key] = next; };
        const historyLabel = `Set ${label}`;

        switch (descriptor.type) {
            case PropertyType.Bool:
                return field(label, this._checkbox(value, commit, historyLabel));

            case PropertyType.Int:
                return field(label, this._trackFieldEdit(historyLabel, numberInput(value, (v) => commit(Math.round(v)),
                    { step: 1, min: descriptor.min, max: descriptor.max })));

            case PropertyType.Number:
                return field(label, this._trackFieldEdit(historyLabel, numberInput(value, commit,
                    { step: descriptor.step ?? 0.1, min: descriptor.min, max: descriptor.max })));

            case PropertyType.String:
                return field(label, this._trackFieldEdit(historyLabel, el('input.sb-text', {
                    type: 'text', value: value ?? '',
                    oninput: (e) => commit(e.target.value),
                })));

            case PropertyType.Asset:
                return field(label, this._trackFieldEdit(historyLabel, el('input.sb-text', {
                    type: 'text',
                    value: value ?? '',
                    placeholder: descriptor.assetKind ?? 'path',
                    oninput: (e) => commit(e.target.value),
                })));

            case PropertyType.Vector2:
                return field(label, ...this._vectorInputs(value, commit, ['x', 'y'], { historyLabel }));

            case PropertyType.Vector3:
                return field(label, ...this._vectorInputs(value, commit, ['x', 'y', 'z'], { historyLabel }));

            case PropertyType.Vector4:
                return field(label, ...this._vectorInputs(value, commit, ['x', 'y', 'z', 'w'], { historyLabel }));

            case PropertyType.Quaternion:
                return field(label, ...this._vectorInputs(
                    Quaternion.toEuler(value), (v) => commit(Quaternion.fromEuler(v)),
                    ['x', 'y', 'z'], { step: 1, historyLabel }));

            case PropertyType.Color:
                return field(label, this._colorInput(value, commit, historyLabel));

            case PropertyType.Enum:
                return field(label, this._trackFieldEdit(historyLabel, el('select.sb-select', {
                    onchange: (e) => commit(e.target.value),
                }, ...(descriptor.values ?? []).map((option) =>
                    el('option', { value: option, text: option, selected: option === value })))));

            case PropertyType.List:
                return field(label, this._listInput(value, descriptor, commit, historyLabel));

            default:
                return field(label, el('span.sb-note-inline', {
                    text: 'Read-only in the browser editor',
                    title: `No portable widget is registered for ${descriptor.type ?? 'this'} values. `
                        + 'The saved value is preserved unchanged.',
                }));
        }
    }

    /**
     * One numeric input per component of a vector.
     *
     * The whole vector is rebuilt and reassigned on each keystroke rather than
     * mutated in place, because a transform only recomputes its world values when
     * its setter runs — writing `position.x` directly leaves the cache stale.
     */
    _vectorInputs(value, commit, axes, options = {}) {
        const read = (axis) => value?.[axis] ?? 0;
        const { historyLabel = null, ...numberOptions } = options;

        return axes.map((axis) => {
            const input = this._trackFieldEdit(historyLabel, numberInput(read(axis), (v) => {
                const next = Object.fromEntries(axes.map((a) => [a, a === axis ? v : read(a)]));
                commit(rebuildVector(value, next, axes));
            }, numberOptions));
            return el('span.sb-axis', {},
                el('i.sb-axis-label', { text: axis, dataset: { axis } }), input);
        });
    }

    _colorInput(value, commit, historyLabel) {
        const colour = Color.from(value);

        const swatch = el('input.sb-color', {
            type: 'color',
            value: colour.toHexRgb(),
            oninput: (e) => {
                const next = Color.fromString(e.target.value);
                next.a = alpha.valueAsNumber * 255;
                commit(next);
            },
        });

        const alpha = el('input.sb-alpha', {
            type: 'range', min: 0, max: 1, step: 0.01,
            value: colour.a / 255,
            title: 'Alpha',
            oninput: () => {
                const next = Color.fromString(swatch.value);
                next.a = alpha.valueAsNumber * 255;
                commit(next);
            },
        });

        return el('span.sb-color-field', {},
            this._trackFieldEdit(historyLabel, swatch),
            this._trackFieldEdit(historyLabel, alpha));
    }

    /**
     * Portable list editing deliberately uses scene-format JSON for now.  That
     * supports every serialisable element shape (including nested vectors) and
     * validates before changing the component, without pretending every family
     * already has a bespoke array UI.  The schema's `of` descriptor rehydrates
     * parsed values into their runtime type.
     */
    _listInput(value, descriptor, commit, historyLabel) {
        const count = el('span.sb-note-inline', {
            text: `${Array.isArray(value) ? value.length : 0} item(s)`,
        });
        const source = el('textarea.sb-list-editor', {
            rows: 3,
            value: formatListValue(value, descriptor),
            title: 'Editable JSON array. Changes are validated before they are applied.',
        });

        const apply = () => {
            try {
                const next = parseListValue(source.value, descriptor);
                source.classList.remove('is-invalid');
                source.setCustomValidity('');
                count.textContent = `${next.length} item(s)`;
                commit(next);
            } catch (error) {
                source.classList.add('is-invalid');
                source.setCustomValidity(error.message);
                this.state.error(error.message);
            }
        };
        source.addEventListener('change', apply);

        return el('span.sb-list-field', {},
            this._trackFieldEdit(historyLabel, source),
            el('span.sb-list-actions', {},
                el('button.sb-btn.sb-btn-small', {
                    type: 'button', text: '+', title: 'Add an item using the schema default',
                    onclick: () => {
                        this.editor.history.snapshot(historyLabel, () => {
                            const next = appendListValue(value, descriptor);
                            commit(next);
                            this.state.markDirty();
                            this.render();
                        });
                    },
                }),
                count));
    }

    _checkbox(checked, commit, historyLabel = null) {
        return this._trackFieldEdit(historyLabel, el('input.sb-check', {
            type: 'checkbox',
            checked: Boolean(checked),
            onchange: (e) => commit(e.target.checked),
        }));
    }

    /** Starts a transaction on focus and closes it on a field commit or blur. */
    _trackFieldEdit(label, control) {
        if (!label) return control;
        control.addEventListener('focus', () => this._beginFieldTransaction(label));
        control.addEventListener('change', () => this._commitFieldTransaction());
        control.addEventListener('blur', () => this._commitFieldTransaction());
        control.addEventListener('keydown', (event) => {
            if (event.key !== 'Escape') return;
            event.preventDefault();
            this._cancelFieldTransaction();
            control.blur();
        });
        return control;
    }

    _beginFieldTransaction(label) {
        if (!this.editor?.history || !this.editor.scene) return;
        this._commitFieldTransaction();
        this._fieldTransaction = {
            label,
            before: this.editor.captureHistorySnapshot(),
            wasDirty: this.state.sceneDirty,
        };
    }

    _commitFieldTransaction() {
        const transaction = this._fieldTransaction;
        if (!transaction) return;
        this._fieldTransaction = null;
        const after = this.editor.captureHistorySnapshot();
        if (transaction.before.scene === after.scene) return;

        this.editor.history.push(transaction.label,
            () => this.editor.restoreHistorySnapshot(transaction.before),
            () => this.editor.restoreHistorySnapshot(after));
        this.state.markDirty();
    }

    _cancelFieldTransaction() {
        const transaction = this._fieldTransaction;
        if (!transaction) return;
        this._fieldTransaction = null;
        this.editor.restoreHistorySnapshot(transaction.before);
        if (!transaction.wasDirty) this.state.markClean();
    }

    _section(title, rows, { onRemove = null, id = null, missing = false } = {}) {
        const section = el(`section.sb-section${missing ? '.is-missing' : ''}`);

        section.append(el('header.sb-section-head', {},
            el('span.sb-section-title', { text: title }),
            id ? el('span.sb-section-id', { text: id }) : null,
            onRemove
                ? el('button.sb-remove', {
                    type: 'button', text: '×', title: 'Remove this component',
                    onclick: onRemove,
                })
                : null));

        const body = el('div.sb-section-body');
        for (const row of rows) if (row) body.append(row);
        section.append(body);

        return section;
    }

    _addComponentControl(actor) {
        const select = el('select.sb-select.sb-add-component', {},
            el('option', { value: '', text: 'Add component…' }));

        let lastCategory = null;
        let group = null;
        for (const entry of listComponents()) {
            if (entry.category !== lastCategory) {
                lastCategory = entry.category;
                group = el('optgroup', { label: entry.category });
                select.append(group);
            }
            group.append(el('option', { value: entry.name, text: entry.name, title: entry.summary }));
        }

        select.addEventListener('change', () => {
            const name = select.value;
            select.value = '';
            if (!name) return;

            try {
                this.editor.history.snapshot(`Add ${name}`, () => {
                    actor.addComponent(name);
                    this.state.markDirty();
                    this.render();
                    this.state.notifyHierarchy();
                });
            } catch (err) {
                this.state.error(`Could not add ${name}: ${err.message}`);
            }
        });

        return el('div.sb-add-row', {}, select);
    }
}

/** Rebuilds a vector of the same kind as the one being edited. */
function rebuildVector(previous, values, axes) {
    if (axes.length === 2) return new Vector2(values.x, values.y);
    if (axes.length === 3) return new Vector3(values.x, values.y, values.z);
    return { ...previous, ...values };
}

/** `gravityScale` becomes `Gravity scale`. */
function humanise(key) {
    const spaced = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
    return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase()
        .replace(/\b(2d|3d|uv|id|fov|dpi)\b/g, (m) => m.toUpperCase());
}

export { formatNumber };
