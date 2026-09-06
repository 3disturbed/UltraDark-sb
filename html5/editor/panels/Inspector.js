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

/** The details panel. */
export class InspectorPanel {
    constructor(state, editor) {
        this.state = state;
        this.editor = editor;
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
            field('Name', el('input.sb-text', {
                type: 'text',
                value: actor.name,
                oninput: (e) => {
                    actor.name = e.target.value;
                    this.state.markDirty();
                    this.state.notifyHierarchy();
                },
            })),
            field('Tag', el('input.sb-text', {
                type: 'text',
                value: actor.tag,
                oninput: (e) => { actor.tag = e.target.value; this.state.markDirty(); },
            })),
            field('Layer', numberInput(actor.layer, (v) => {
                actor.layer = Math.round(v);
                this.state.markDirty();
            }, { step: 1 })),
            field('Active', this._checkbox(actor.isActive, (v) => {
                actor.isActive = v;
                this.state.markDirty();
                this.state.notifyHierarchy();
            })),
            field('Life span', numberInput(actor.lifeSpan, (v) => {
                actor.lifeSpan = v;
                this.state.markDirty();
            }, { step: 0.1, min: 0 })),
        ], { id: `Actor #${actor.id}` });
    }

    _transformSection(actor) {
        const t = actor.transform;
        return this._section('Transform (2D)', [
            field('Position', ...this._vectorInputs(t.localPosition, (v) => {
                t.localPosition = v;
                this.state.markDirty();
            }, ['x', 'y'])),
            field('Rotation', numberInput(t.localRotation * 180 / Math.PI, (v) => {
                t.localRotation = v * Math.PI / 180;
                this.state.markDirty();
            }, { step: 1 }), el('span.sb-unit', { text: '°' })),
            field('Scale', ...this._vectorInputs(t.localScale, (v) => {
                t.localScale = v;
                this.state.markDirty();
            }, ['x', 'y'])),
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
                this.state.markDirty();
            }, ['x', 'y', 'z'])),
            field('Rotation', ...this._vectorInputs(euler, (v) => {
                t.localEulerAngles = v;
                this.state.markDirty();
            }, ['x', 'y', 'z'], { step: 1 })),
            field('Scale', ...this._vectorInputs(t.localScale, (v) => {
                t.localScale = v;
                this.state.markDirty();
            }, ['x', 'y', 'z'])),
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
            field('Enabled', this._checkbox(component.enabled, (v) => {
                component.enabled = v;
                this.state.markDirty();
            })),
        ];

        for (const [key, descriptor] of Object.entries(schema)) {
            if (key === 'enabled' || descriptor.hidden || descriptor.transient) continue;
            rows.push(this._propertyField(component, key, descriptor));
        }

        return this._section(name, rows, {
            onRemove: () => {
                actor.removeComponent(component);
                this.state.markDirty();
                this.render();
                this.state.notifyHierarchy();
            },
        });
    }

    // ---- Fields -------------------------------------------------------------

    _propertyField(target, key, descriptor) {
        const label = humanise(key);
        const value = target[key];
        const commit = (next) => { target[key] = next; this.state.markDirty(); };

        switch (descriptor.type) {
            case PropertyType.Bool:
                return field(label, this._checkbox(value, commit));

            case PropertyType.Int:
                return field(label, numberInput(value, (v) => commit(Math.round(v)),
                    { step: 1, min: descriptor.min, max: descriptor.max }));

            case PropertyType.Number:
                return field(label, numberInput(value, commit,
                    { step: descriptor.step ?? 0.1, min: descriptor.min, max: descriptor.max }));

            case PropertyType.String:
                return field(label, el('input.sb-text', {
                    type: 'text', value: value ?? '',
                    oninput: (e) => commit(e.target.value),
                }));

            case PropertyType.Asset:
                return field(label, el('input.sb-text', {
                    type: 'text',
                    value: value ?? '',
                    placeholder: descriptor.assetKind ?? 'path',
                    oninput: (e) => commit(e.target.value),
                }));

            case PropertyType.Vector2:
                return field(label, ...this._vectorInputs(value, commit, ['x', 'y']));

            case PropertyType.Vector3:
                return field(label, ...this._vectorInputs(value, commit, ['x', 'y', 'z']));

            case PropertyType.Vector4:
                return field(label, ...this._vectorInputs(value, commit, ['x', 'y', 'z', 'w']));

            case PropertyType.Quaternion:
                return field(label, ...this._vectorInputs(
                    Quaternion.toEuler(value), (v) => commit(Quaternion.fromEuler(v)),
                    ['x', 'y', 'z'], { step: 1 }));

            case PropertyType.Color:
                return field(label, this._colorInput(value, commit));

            case PropertyType.Enum:
                return field(label, el('select.sb-select', {
                    onchange: (e) => commit(e.target.value),
                }, ...(descriptor.values ?? []).map((option) =>
                    el('option', { value: option, text: option, selected: option === value }))));

            case PropertyType.List:
                return field(label, el('span.sb-note-inline', {
                    text: `${Array.isArray(value) ? value.length : 0} item(s)`,
                    title: 'Lists are preserved on save but not editable here yet.',
                }));

            default:
                return field(label, el('span.sb-note-inline', { text: String(value) }));
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

        return axes.map((axis) => el('span.sb-axis', {},
            el('i.sb-axis-label', { text: axis, dataset: { axis } }),
            numberInput(read(axis), (v) => {
                const next = Object.fromEntries(axes.map((a) => [a, a === axis ? v : read(a)]));
                commit(rebuildVector(value, next, axes));
            }, options)));
    }

    _colorInput(value, commit) {
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

        return el('span.sb-color-field', {}, swatch, alpha);
    }

    _checkbox(checked, commit) {
        return el('input.sb-check', {
            type: 'checkbox',
            checked: Boolean(checked),
            onchange: (e) => commit(e.target.checked),
        });
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
                actor.addComponent(name);
                this.state.markDirty();
                this.render();
                this.state.notifyHierarchy();
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
