// -----------------------------------------------------------------------------
// TypeRegistry — name to constructor, for components and actor subclasses.
//
// The C# engine finds types by scanning every loaded assembly, which is how a
// scene file gets away with naming a component `"SpriteRenderer"` and nothing
// else. JavaScript has no equivalent, so types register themselves here and the
// registry reproduces the same resolution ladder: assembly-qualified name with
// everything after the first comma stripped, then namespace-qualified, then the
// bare short name. That keeps existing `.scene` files loadable unchanged,
// including ones written by an older C# build.
// -----------------------------------------------------------------------------

const _components = new Map();   // short name  -> entry
const _componentsFull = new Map(); // full name -> entry
const _actors = new Map();
const _actorsFull = new Map();
const _schemaCache = new WeakMap();

/**
 * Registers a component class so scene files and tools can name it.
 *
 * @param {Function} ctor The component class.
 * @param {object}  [meta]
 * @param {string}  [meta.name]      Short name. Defaults to `ctor.name`.
 * @param {string}  [meta.fullName]  Namespace-qualified name, for files written by the C# engine.
 * @param {string}  [meta.category]  Grouping for the editor's Add Component menu.
 * @param {string}  [meta.source]    'engine' or 'project'.
 * @param {string}  [meta.summary]   One-line description shown in tooling.
 * @returns {Function} `ctor`, so the call can wrap a class declaration.
 */
export function registerComponent(ctor, meta = {}) {
    const name = meta.name ?? ctor.name;
    const entry = {
        ctor,
        name,
        fullName: meta.fullName ?? `SexyBiscuit.Engine.${meta.category ?? 'Core'}.${name}`,
        category: meta.category ?? 'Core',
        source: meta.source ?? 'engine',
        summary: meta.summary ?? '',
        hidden: meta.hidden === true,
    };

    // The class carries its own registered name so `component.constructor.componentName`
    // is available without a registry lookup — the serialiser writes it per component.
    Object.defineProperty(ctor, 'componentName', { value: name, configurable: true });

    _components.set(name, entry);
    _componentsFull.set(entry.fullName, entry);
    return ctor;
}

/** Registers an actor subclass, so a scene's `"class"` field can name it. */
export function registerActor(ctor, meta = {}) {
    const name = meta.name ?? ctor.name;
    const entry = {
        ctor,
        name,
        fullName: meta.fullName ?? `SexyBiscuit.Engine.${meta.category ?? 'Core'}.${name}`,
        category: meta.category ?? 'Core',
        source: meta.source ?? 'engine',
        summary: meta.summary ?? '',
        hidden: meta.hidden === true,
    };

    Object.defineProperty(ctor, 'actorName', { value: name, configurable: true });

    _actors.set(name, entry);
    _actorsFull.set(entry.fullName, entry);
    return ctor;
}

/**
 * Finds a component class by the name a scene file or tool used.
 * Returns null rather than throwing — an unresolved type becomes a
 * MissingComponent so the data survives the round trip.
 */
export function resolveComponent(name) {
    return resolve(name, _components, _componentsFull);
}

/** Finds an actor subclass by name, or null. */
export function resolveActor(name) {
    return resolve(name, _actors, _actorsFull);
}

function resolve(name, byShort, byFull) {
    if (!name) return null;

    // "Ns.Type, Assembly, Version=..." — everything from the first comma is the
    // assembly identity, which means nothing here.
    const bare = String(name).split(',')[0].trim();

    const full = byFull.get(bare);
    if (full) return full.ctor;

    const short = byShort.get(bare);
    if (short) return short.ctor;

    // A namespace-qualified name whose namespace we do not use: take the last segment.
    const dot = bare.lastIndexOf('.');
    if (dot >= 0) {
        const tail = byShort.get(bare.substring(dot + 1));
        if (tail) return tail.ctor;
    }

    // Last resort, case-insensitive, matching the C# lookup's final fallback.
    const lower = bare.toLowerCase();
    for (const [key, entry] of byShort) {
        if (key.toLowerCase() === lower) return entry.ctor;
    }

    return null;
}

/** The registered short name for a component instance or class. */
export function componentNameOf(componentOrCtor) {
    const ctor = typeof componentOrCtor === 'function' ? componentOrCtor : componentOrCtor?.constructor;
    return ctor?.componentName ?? ctor?.name ?? 'Component';
}

/** Every registered component, for the editor's Add Component list and the tool layer. */
export function listComponents({ includeHidden = false } = {}) {
    return [..._components.values()]
        .filter((e) => includeHidden || !e.hidden)
        .sort((a, b) => a.category.localeCompare(b.category) || a.name.localeCompare(b.name));
}

/** Every registered actor subclass. */
export function listActors({ includeHidden = false } = {}) {
    return [..._actors.values()]
        .filter((e) => includeHidden || !e.hidden)
        .sort((a, b) => a.name.localeCompare(b.name));
}

/** The registry entry for a component class, or undefined. */
export function componentEntry(nameOrCtor) {
    const name = typeof nameOrCtor === 'function' ? componentNameOf(nameOrCtor) : nameOrCtor;
    return _components.get(name);
}

/**
 * The merged property schema for a component class: its own `static schema`
 * layered over every ancestor's, so a subclass inherits its base's properties
 * the way reflection over an inheritance chain does in C#.
 */
export function schemaOf(ctor) {
    if (!ctor) return {};

    const cached = _schemaCache.get(ctor);
    if (cached) return cached;

    // Walk to the root first so nearer declarations overwrite further ones.
    const chain = [];
    for (let c = ctor; c && c !== Function.prototype; c = Object.getPrototypeOf(c)) {
        if (Object.prototype.hasOwnProperty.call(c, 'schema')) chain.unshift(c.schema);
    }

    const merged = Object.assign({}, ...chain);
    _schemaCache.set(ctor, merged);
    return merged;
}

/** Forgets cached schemas. Call after hot-reloading game code. */
export function clearSchemaCache() {
    // A WeakMap has no clear(); replacing entries as they are requested is enough,
    // but a reloaded class is a new object anyway, so nothing stale can be reached.
}

/** Drops every registration whose source is 'project'. Used when reloading game code. */
export function unregisterProjectTypes() {
    for (const [key, entry] of [..._components]) {
        if (entry.source === 'project') { _components.delete(key); _componentsFull.delete(entry.fullName); }
    }
    for (const [key, entry] of [..._actors]) {
        if (entry.source === 'project') { _actors.delete(key); _actorsFull.delete(entry.fullName); }
    }
}
