// -----------------------------------------------------------------------------
// SceneSerializer — reads and writes the `.scene` JSON the C# engine uses.
//
// This is the interoperability contract: a scene saved by either engine must
// load in the other, unchanged. Everything here follows `Scene/SceneSerializer.cs`
// rather than what would be natural in JavaScript — PascalCase property keys,
// `[x, y]` vectors, `#RRGGBBAA` colours, quaternions as raw components, and the
// exact order in which the several transform encodings override one another.
//
// The reader is deliberately more forgiving than the writer. Every bundled
// template writes the hand-authored `"transform": { "x": …, "y": … }` object and
// `{ "R": …, "G": … }` colours, neither of which the C# writer ever emits, so
// both forms have to load.
// -----------------------------------------------------------------------------

// Loading the built-ins is what makes `"type": "SpriteRenderer"` resolve to a
// class. Without it every component in a loaded scene becomes a MissingComponent.
import '../registerBuiltins.js';

import { Scene } from '../core/Scene.js';
import { Actor } from '../core/Actor.js';
import { Transform3D } from '../core/Transform3D.js';
import { MissingComponent, MissingActorClass } from '../core/MissingComponent.js';
import {
    resolveComponent, resolveActor, componentNameOf, schemaOf,
} from '../core/TypeRegistry.js';
import { coerce, encode } from '../core/PropertyTypes.js';
import { Vector2, Vector3, Quaternion } from '../math/index.js';

// -----------------------------------------------------------------------------
// Reading
// -----------------------------------------------------------------------------

/**
 * Builds a Scene from `.scene` JSON.
 *
 * @param {string|object} json The file's text, or the already-parsed object.
 * @param {object} [options]
 * @param {(message: string) => void} [options.onWarning] Called for each recoverable problem.
 * @param {boolean} [options.flush=true] Apply the queued actors, which starts them.
 *   Pass false when the caller still has to attach the scene to an engine:
 *   starting an actor before its scene has a host means anything that loads an
 *   asset in `start` — a sprite's texture, a script's source — finds no asset
 *   manager and silently never loads.
 * @returns {Scene}
 */
export function deserialize(json, options = {}) {
    const dto = typeof json === 'string' ? JSON.parse(json) : json;
    const warn = options.onWarning ?? ((m) => console.warn(`[SceneSerializer] ${m}`));

    // Layers are created explicitly from the file, so a scene that names only
    // "Default" does not silently gain the four standard ones.
    const scene = new Scene(dto?.name ?? 'Scene', { createDefaultLayers: false });

    for (const layerDto of dto?.layers ?? []) {
        const layer = scene.getOrCreateLayer(layerDto?.name ?? 'default', layerDto?.order ?? 0);
        for (const actorDto of layerDto?.actors ?? []) {
            scene.addActor(buildActor(actorDto, warn), layer.name);
        }
    }

    // A file with no layers at all still needs somewhere to put things.
    if (scene.layers.length === 0) scene.addLayer('default', 0);

    if (options.flush !== false) scene.flushPendingActors();
    return scene;
}

/**
 * Builds one Actor from its DTO. Exported because a prefab file is exactly this
 * shape — a single entry from a scene's `actors` array.
 */
export function buildActor(dto, warn = () => {}) {
    const actor = constructActor(dto, warn);

    actor.name = dto?.name ?? 'Actor';
    actor.tag = dto?.tag ?? 'Untagged';
    actor.layer = dto?.layer ?? 0;
    actor.isActive = dto?.active ?? true;
    actor.lifeSpan = dto?.lifeSpan ?? 0;

    if (dto?.properties && !actor.getComponent(MissingActorClass)) {
        applyProperties(actor, dto.properties, dto.class ?? 'Actor', warn);
    }

    // ---- 2D transform: flat arrays first ----
    actor.transform.localPosition = Array.isArray(dto?.position) && dto.position.length >= 2
        ? new Vector2(dto.position[0], dto.position[1])
        : new Vector2(0, 0);
    actor.transform.localRotation = dto?.rotation ?? 0;
    actor.transform.localScale = Array.isArray(dto?.scale) && dto.scale.length >= 2
        ? new Vector2(dto.scale[0], dto.scale[1])
        : new Vector2(1, 1);

    // The readable object form overrides the arrays when both are present.
    const t2 = dto?.transform;
    if (t2) {
        actor.transform.localPosition = new Vector2(t2.x ?? 0, t2.y ?? 0);
        actor.transform.localRotation = t2.rotation ?? 0;
        actor.transform.localScale = new Vector2(t2.scaleX ?? 1, t2.scaleY ?? 1);
    }

    // ---- 3D transform: the object form, in euler degrees ----
    const t3 = dto?.transform3d ?? dto?.transform3D;
    if (t3) {
        const t = actor.getComponent(Transform3D) ?? actor.addComponent(Transform3D);
        t.localPosition = new Vector3(t3.x ?? 0, t3.y ?? 0, t3.z ?? 0);
        t.localEulerAngles = new Vector3(t3.rotX ?? 0, t3.rotY ?? 0, t3.rotZ ?? 0);
        t.localScale = new Vector3(t3.scaleX ?? 1, t3.scaleY ?? 1, t3.scaleZ ?? 1);
    }

    // The quaternion arrays run last, so they win over `transform3d` when a file
    // carries both — matching the C# load order exactly. A 3D transform is added
    // only when the file actually has one, so a 2D actor stays 2D.
    if (dto?.position3 || dto?.rotation3 || dto?.scale3) {
        const t = actor.getComponent(Transform3D) ?? actor.addComponent(Transform3D);
        if (Array.isArray(dto.position3) && dto.position3.length >= 3) {
            t.localPosition = Vector3.from(dto.position3);
        }
        if (Array.isArray(dto.rotation3) && dto.rotation3.length >= 4) {
            t.localRotation = Quaternion.from(dto.rotation3);
        }
        if (Array.isArray(dto.scale3) && dto.scale3.length >= 3) {
            t.localScale = Vector3.from(dto.scale3);
        }
    }

    // ---- Components, in file order ----
    // A component the actor already has — added by a subclass constructor, or the
    // Transform3D the transform block created — is filled in rather than
    // duplicated. Each instance is claimed once, so two genuine components of one
    // type still load as two.
    const claimed = new Set();

    for (const compDto of dto?.components ?? []) {
        const typeName = compDto?.type;
        if (!typeName) continue;

        const ctor = resolveComponent(typeName);

        if (!ctor) {
            warn(`Could not resolve component type '${typeName}'. Kept as a MissingComponent.`);
            const placeholder = actor.addComponent(MissingComponent);
            placeholder.typeName = typeName;
            placeholder.properties = compDto.properties ?? null;
            claimed.add(placeholder);
            continue;
        }

        let component = null;
        for (const existing of actor.getAllComponents()) {
            if (existing.constructor === ctor && !claimed.has(existing)) { component = existing; break; }
        }

        if (!component) {
            try {
                component = actor.addComponentByType(ctor);
            } catch (err) {
                warn(`Failed to create component '${typeName}': ${err.message}`);
                continue;
            }
        }

        claimed.add(component);
        if (compDto.properties) {
            applyProperties(component, compDto.properties, typeName, warn);
        }
    }

    return actor;
}

function constructActor(dto, warn) {
    const className = dto?.class;
    if (!className) return new Actor();

    const ctor = resolveActor(className);
    if (ctor) {
        try {
            return new ctor();
        } catch (err) {
            warn(`Could not construct actor class '${className}': ${err.message}. Loading as a plain Actor.`);
        }
    }

    const actor = new Actor();
    const marker = actor.addComponent(MissingActorClass);
    marker.className = className;
    marker.properties = dto.properties ?? null;
    return actor;
}

/**
 * Writes a property bag onto a component or actor, coercing each value to the
 * type its schema declares.
 *
 * Keys are matched case-insensitively, so the PascalCase a C# file uses (`Tint`)
 * finds the camelCase the schema declares (`tint`).
 */
export function applyProperties(target, properties, ownerName, warn = () => {}) {
    const schema = schemaOf(target.constructor);
    const lookup = buildKeyLookup(schema);

    for (const [rawKey, rawValue] of Object.entries(properties)) {
        const key = lookup.get(rawKey.toLowerCase());

        if (!key) {
            warn(`'${ownerName}' has no property '${rawKey}'. Ignored.`);
            continue;
        }

        try {
            target[key] = coerce(rawValue, schema[key]);
        } catch (err) {
            warn(`Could not set '${rawKey}' on '${ownerName}': ${err.message}`);
        }
    }
}

/** Maps every accepted spelling of a schema key to the canonical one. */
function buildKeyLookup(schema) {
    const lookup = new Map();
    for (const [key, descriptor] of Object.entries(schema)) {
        lookup.set(key.toLowerCase(), key);
        if (descriptor.jsonName) lookup.set(descriptor.jsonName.toLowerCase(), key);
    }
    return lookup;
}

// -----------------------------------------------------------------------------
// Writing
// -----------------------------------------------------------------------------

/**
 * Serialises a Scene to `.scene` JSON that the C# engine reads.
 *
 * @param {Scene} scene
 * @param {object} [options]
 * @param {number} [options.indent=2] Passed to JSON.stringify.
 * @returns {string}
 */
export function serialize(scene, options = {}) {
    return JSON.stringify(buildSceneDto(scene), null, options.indent ?? 2);
}

/** The plain object a scene serialises to, before it is stringified. */
export function buildSceneDto(scene) {
    return {
        name: scene.name,
        layers: scene.layers.map((layer) => ({
            name: layer.name,
            order: layer.order,
            actors: layer.actors.map(buildActorDto),
        })),
    };
}

/** The plain object one actor serialises to. This is also the prefab format. */
export function buildActorDto(actor) {
    const components = [];

    for (const component of actor.getAllComponents()) {
        // Transforms live in flat fields on the actor, not in the component list,
        // and the missing-class marker is written back as the actor's `class`.
        if (component === actor.transform) continue;
        if (component instanceof Transform3D) continue;
        if (component instanceof MissingActorClass) continue;

        // A placeholder goes back out exactly as it came in.
        if (component instanceof MissingComponent) {
            components.push({ type: component.typeName, properties: component.properties ?? {} });
            continue;
        }

        components.push({
            type: componentNameOf(component),
            properties: collectProperties(component),
        });
    }

    const missingClass = actor.getComponent(MissingActorClass);
    let className = null;
    let actorProperties = null;

    if (missingClass) {
        className = missingClass.className;
        actorProperties = missingClass.properties;
    } else if (actor.constructor !== Actor) {
        className = actor.constructor.actorName ?? actor.constructor.name;
        actorProperties = collectProperties(actor);
        if (Object.keys(actorProperties).length === 0) actorProperties = null;
    }

    const t = actor.transform;
    const dto = {
        name: actor.name,
        tag: actor.tag,
        layer: actor.layer,
        active: actor.isActive,
        position: [round4(t.localPosition.x), round4(t.localPosition.y)],
        rotation: round4(t.localRotation),
        scale: [round4(t.localScale.x), round4(t.localScale.y)],
        components,
    };

    // `class` and `properties` come first in the C# output; rebuilding the object
    // in that order keeps a diff between the two engines' saves readable.
    if (className) {
        return withLeading({ class: className, properties: actorProperties ?? undefined },
            dto, actor);
    }

    return finishActorDto(dto, actor);
}

function withLeading(leading, dto, actor) {
    const { name, ...rest } = dto;
    return finishActorDto({ name, ...leading, ...rest }, actor);
}

function finishActorDto(dto, actor) {
    if (actor.lifeSpan > 0) dto.lifeSpan = round4(actor.lifeSpan);

    const t3 = actor.getComponent(Transform3D);
    if (t3) {
        dto.position3 = t3.localPosition.toArray().map(round4);
        dto.rotation3 = t3.localRotation.toArray().map(round4);
        dto.scale3 = t3.localScale.toArray().map(round4);
    }

    // Drop the keys the C# writer omits when they carry no information, so files
    // written by the two engines compare cleanly.
    if (dto.properties === undefined) delete dto.properties;
    return dto;
}

/** Reads every schema-declared property of a component or actor into a JSON bag. */
export function collectProperties(target) {
    const schema = schemaOf(target.constructor);
    const bag = {};

    for (const [key, descriptor] of Object.entries(schema)) {
        if (descriptor.transient) continue;

        const value = target[key];
        if (value === undefined || value === null) continue;

        // PascalCase on the way out: that is what the C# properties bag uses, and
        // its reader is case-insensitive but its writer is not.
        const jsonKey = descriptor.jsonName ?? key.charAt(0).toUpperCase() + key.slice(1);
        bag[jsonKey] = encode(value, descriptor);
    }

    return bag;
}

function round4(n) { return Math.round(n * 1e4) / 1e4; }

/** Convenience: `SceneSerializer.serialize(...)` reads the way the C# call site does. */
export const SceneSerializer = {
    serialize,
    deserialize,
    buildSceneDto,
    buildActorDto,
    buildActor,
    collectProperties,
    applyProperties,
};
