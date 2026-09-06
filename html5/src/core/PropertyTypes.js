// -----------------------------------------------------------------------------
// PropertyTypes — the typed property schema components declare, and the coercion
// that reads and writes it.
//
// The C# engine discovers a component's saveable state by reflecting over public
// get/set properties and checking the type against a whitelist. JavaScript has no
// equivalent — a plain field carries no type — so components declare a `schema`
// instead. That gets three things at once: the serialiser knows how to turn
// `"Size": [32, 48]` into a Vector2, the editor inspector knows to draw two
// number boxes, and a tool can list what is editable, exactly as
// `ComponentReflection.EditableProperties` does in C#.
// -----------------------------------------------------------------------------

import { Vector2, Vector3, Vector4, Quaternion, Color } from '../math/index.js';

/** The property kinds a component may declare. Mirrors `ValueConverter.IsSupportedType`. */
export const PropertyType = {
    Number: 'number',
    Int: 'int',
    Bool: 'bool',
    String: 'string',
    Vector2: 'vector2',
    Vector3: 'vector3',
    Vector4: 'vector4',
    Quaternion: 'quaternion',
    Color: 'color',
    Enum: 'enum',
    Asset: 'asset',
    Material: 'material',
    List: 'list',
};

/**
 * Turns a JSON value into the runtime type a property expects.
 *
 * Deliberately lenient, like the C# `ValueConverter`: a vector accepts an array
 * or an object, a colour accepts hex, channels or a name, an enum accepts a name
 * or an ordinal. A scene written by hand should load.
 *
 * @param {*} value The raw JSON value.
 * @param {object} descriptor The schema entry describing the property.
 * @returns {*} The coerced value, or the descriptor's default when unusable.
 */
export function coerce(value, descriptor) {
    const type = descriptor?.type ?? PropertyType.Number;

    switch (type) {
        case PropertyType.Number:
            return typeof value === 'number' ? value : (parseFloat(value) || 0);

        case PropertyType.Int: {
            const n = typeof value === 'number' ? value : parseFloat(value);
            return Number.isFinite(n) ? Math.round(n) : 0;
        }

        case PropertyType.Bool:
            if (typeof value === 'boolean') return value;
            if (typeof value === 'number') return value !== 0;
            if (typeof value === 'string') return value.toLowerCase() === 'true';
            return false;

        case PropertyType.String:
        case PropertyType.Asset:
            return value == null ? '' : String(value);

        case PropertyType.Vector2:    return Vector2.from(value);
        case PropertyType.Vector3:    return Vector3.from(value);
        case PropertyType.Vector4:    return Vector4.from(value);
        case PropertyType.Color:      return Color.from(value);

        case PropertyType.Quaternion:
            // Three numbers mean euler degrees — the form tools and the inspector
            // use. Four mean the raw components a scene file stores.
            if (Array.isArray(value) && value.length === 3) return Quaternion.fromEuler(value);
            if (value && typeof value === 'object' && !Array.isArray(value)
                && (value.pitch !== undefined || value.yaw !== undefined || value.roll !== undefined)) {
                return Quaternion.fromEuler([value.pitch ?? 0, value.yaw ?? 0, value.roll ?? 0]);
            }
            return Quaternion.from(value);

        case PropertyType.Enum: {
            const allowed = descriptor.values ?? [];
            if (typeof value === 'number') return allowed[value] ?? descriptor.default ?? allowed[0];
            const text = String(value);
            const match = allowed.find((v) => v.toLowerCase() === text.toLowerCase());
            return match ?? descriptor.default ?? allowed[0];
        }

        case PropertyType.List: {
            if (!Array.isArray(value)) return [];
            const element = descriptor.of ?? { type: PropertyType.Number };
            return value.map((item) => coerce(item, element));
        }

        case PropertyType.Material:
            // Imported lazily by the caller that owns Material3D, to keep this
            // module free of rendering dependencies.
            return value;

        default:
            return value;
    }
}

/**
 * Turns a runtime value into the JSON a `.scene` file stores.
 *
 * This is the C# scene-file encoding, not the tool-facing one: quaternions go out
 * as `[x, y, z, w]` and colours as `#RRGGBBAA`.
 */
export function encode(value, descriptor) {
    if (value == null) return null;
    const type = descriptor?.type ?? PropertyType.Number;

    switch (type) {
        case PropertyType.Number:
            // Four decimal places, matching the C# tool layer, so a scene file does
            // not fill with floating-point noise on every save.
            return Math.abs(value % 1) < 1e-9 ? value : Math.round(value * 1e4) / 1e4;

        case PropertyType.Vector2:
        case PropertyType.Vector3:
        case PropertyType.Vector4:
        case PropertyType.Quaternion:
            return value.toArray ? value.toArray().map(round4) : value;

        case PropertyType.Color:
            return value.toHex ? value.toHex() : value;

        case PropertyType.List:
            return value.map((item) => encode(item, descriptor.of ?? { type: PropertyType.Number }));

        case PropertyType.Material:
            return value?.toJSON ? value.toJSON() : value;

        default:
            return value;
    }
}

function round4(n) { return Math.round(n * 1e4) / 1e4; }

/** A fresh copy of a descriptor's default, so instances never share a mutable value. */
export function defaultValue(descriptor) {
    const raw = descriptor.default;
    if (raw === undefined) {
        switch (descriptor.type) {
            case PropertyType.Bool:       return false;
            case PropertyType.String:
            case PropertyType.Asset:      return '';
            case PropertyType.Vector2:    return new Vector2();
            case PropertyType.Vector3:    return new Vector3();
            case PropertyType.Vector4:    return new Vector4();
            case PropertyType.Quaternion: return Quaternion.identity;
            case PropertyType.Color:      return Color.white;
            case PropertyType.Enum:       return (descriptor.values ?? [])[0] ?? '';
            case PropertyType.List:       return [];
            default:                      return 0;
        }
    }

    // Run literals through coerce so a schema can write `default: [1, 1]` for a
    // Vector2 and still get a Vector2 instance per component.
    if (typeof raw === 'function') return raw();
    return coerce(raw, descriptor);
}
