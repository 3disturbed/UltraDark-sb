// -----------------------------------------------------------------------------
// Inspector collection values — the DOM-free half of list editing.
//
// Keeping parsing and coercion out of InspectorPanel makes the serialisable
// collection contract testable without a browser.  A list still uses the
// element descriptor from its component schema, so vectors, colours and enums
// return to their runtime representations rather than becoming raw JSON.
// -----------------------------------------------------------------------------

import { PropertyType, coerce, defaultValue, encode } from '../src/core/PropertyTypes.js';

/** Returns the scene-format JSON displayed by the collection field. */
export function formatListValue(value, descriptor) {
    return JSON.stringify(encode(Array.isArray(value) ? value : [], descriptor), null, 2);
}

/**
 * Validates and rehydrates user-entered collection JSON.
 *
 * @throws {Error} when the source is not JSON or is not an array.
 */
export function parseListValue(source, descriptor) {
    let parsed;
    try {
        parsed = JSON.parse(source);
    } catch {
        throw new Error('Enter a valid JSON array.');
    }
    if (!Array.isArray(parsed)) throw new Error('A collection value must be a JSON array.');
    return coerce(parsed, descriptor);
}

/** Adds a schema-default item without sharing a mutable default between rows. */
export function appendListValue(value, descriptor) {
    const element = descriptor?.of ?? { type: PropertyType.Number };
    return [...(Array.isArray(value) ? value : []), defaultValue(element)];
}
