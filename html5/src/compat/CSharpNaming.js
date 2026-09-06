// -----------------------------------------------------------------------------
// CSharpNaming — optional PascalCase aliases on the engine's classes.
//
// The JavaScript API is camelCase, as JavaScript is. That means a C# component
// ports by lowering the first letter of each member — mechanical, but still an
// edit on every line. Calling `installCSharpAliases()` adds PascalCase aliases
// so transliterated C# runs as written:
//
//     actor.Transform.Position = new Vector2(10, 0);
//     GetComponent(Rigidbody2D).AddForce(...);
//
// The aliases are non-enumerable, so they are invisible to `Object.keys`,
// `for...in` and `JSON.stringify` — a scene file gains nothing from them and the
// inspector still shows one entry per property.
//
// This is opt-in. Leaving it off keeps one obvious spelling for each member,
// which is the better default for code written here rather than ported.
// -----------------------------------------------------------------------------

import { Actor } from '../core/Actor.js';
import { Component } from '../core/Component.js';
import { Scene } from '../core/Scene.js';
import { Layer } from '../core/Layer.js';
import { Transform } from '../core/Transform.js';
import { Transform3D } from '../core/Transform3D.js';
import { listComponents, listActors } from '../core/TypeRegistry.js';

let _installed = false;

/**
 * Installs PascalCase aliases on the engine's object model and on every
 * registered component and actor class.
 *
 * Call it once, after your own components have registered — a class registered
 * later gets aliases only if you call this again.
 *
 * @returns {number} How many aliases were added.
 */
export function installCSharpAliases() {
    const classes = [
        Actor, Component, Scene, Layer, Transform, Transform3D,
        ...listComponents({ includeHidden: true }).map((e) => e.ctor),
        ...listActors({ includeHidden: true }).map((e) => e.ctor),
    ];

    let added = 0;
    for (const ctor of new Set(classes)) {
        added += aliasPrototype(ctor.prototype);
        added += aliasConstructorFields(ctor);
    }

    _installed = true;
    return added;
}

/** True once {@link installCSharpAliases} has run. */
export function areCSharpAliasesInstalled() { return _installed; }

/**
 * Adds prototype-level aliases for the fields a class assigns in its constructor.
 *
 * The members that matter most for porting — `actor.transform`, `actor.name`,
 * `component.enabled` — are plain instance fields, not prototype accessors, so
 * walking the prototype alone misses them. A throwaway instance reveals the
 * field names; the alias itself still lives on the prototype and forwards
 * through `this`, so every instance gets it, including ones built before this
 * ran.
 *
 * Constructing a sample is safe for this engine's classes: components register
 * themselves in `awake`, not in their constructors. A class that cannot be
 * constructed without arguments is skipped rather than allowed to throw.
 */
function aliasConstructorFields(ctor) {
    let sample;
    try {
        sample = new ctor();
    } catch {
        return 0;
    }

    let added = 0;
    for (const name of Object.getOwnPropertyNames(sample)) {
        if (!/^[a-z]/.test(name) || name.startsWith('_')) continue;

        const pascal = name.charAt(0).toUpperCase() + name.slice(1);
        if (pascal === name || pascal in ctor.prototype) continue;

        Object.defineProperty(ctor.prototype, pascal, {
            get() { return this[name]; },
            set(value) { this[name] = value; },
            enumerable: false,
            configurable: true,
        });
        added++;
    }

    return added;
}

/**
 * Walks one prototype chain and adds a PascalCase alias for each camelCase
 * member, stopping before Object.prototype.
 */
function aliasPrototype(prototype) {
    let added = 0;

    for (let proto = prototype; proto && proto !== Object.prototype; proto = Object.getPrototypeOf(proto)) {
        for (const name of Object.getOwnPropertyNames(proto)) {
            if (name === 'constructor') continue;
            if (!/^[a-z]/.test(name)) continue;          // already PascalCase, or private
            if (name.startsWith('_')) continue;          // internals stay internal

            const pascal = name.charAt(0).toUpperCase() + name.slice(1);
            if (pascal === name) continue;
            if (pascal in proto) continue;               // a real member already owns the name

            const descriptor = Object.getOwnPropertyDescriptor(proto, name);
            if (!descriptor) continue;

            Object.defineProperty(proto, pascal, toAlias(descriptor, name));
            added++;
        }
    }

    return added;
}

/**
 * Builds the alias descriptor.
 *
 * Accessors are forwarded through `this[name]` rather than by copying the
 * original getter, so an alias on a base class still reaches a subclass's
 * override. Methods are shared by reference, which is enough because they
 * dispatch on `this` anyway.
 */
function toAlias(descriptor, name) {
    if (descriptor.get || descriptor.set) {
        return {
            get: descriptor.get ? function aliasGet() { return this[name]; } : undefined,
            set: descriptor.set ? function aliasSet(value) { this[name] = value; } : undefined,
            enumerable: false,
            configurable: true,
        };
    }

    return {
        value: descriptor.value,
        writable: descriptor.writable ?? true,
        enumerable: false,
        configurable: true,
    };
}

/**
 * Adds aliases for an object's own data fields — the ones assigned in a
 * constructor, which live on the instance rather than the prototype.
 *
 * Instance fields cannot be aliased on the prototype ahead of time, so a class
 * whose state is plain fields (`this.speed = 5`) needs this in its constructor
 * to expose `Speed`. Prototype accessors and methods do not.
 */
export function aliasInstanceFields(instance) {
    for (const name of Object.getOwnPropertyNames(instance)) {
        if (!/^[a-z]/.test(name) || name.startsWith('_')) continue;

        const pascal = name.charAt(0).toUpperCase() + name.slice(1);
        if (pascal === name || pascal in instance) continue;

        Object.defineProperty(instance, pascal, {
            get() { return this[name]; },
            set(value) { this[name] = value; },
            enumerable: false,
            configurable: true,
        });
    }
    return instance;
}
