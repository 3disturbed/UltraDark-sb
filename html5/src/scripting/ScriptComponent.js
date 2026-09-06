// -----------------------------------------------------------------------------
// ScriptComponent — runs a project `.js` file against one actor.
//
// Two script styles are supported from the same component:
//
//   * the flat-function style the C# engine's Jint bridge defines, where the
//     file declares `onStart`, `onUpdate` and friends at top level and reads the
//     `actor`, `transform`, `Input`, `Scene`, `Audio`, `Debug` and `Vector2`
//     globals — the form every existing project script is written in;
//
//   * an ES module exporting a Component subclass, which is the class-based API
//     this engine prefers for new work.
//
// The style is detected from the source, so a project can mix the two.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { createScriptGlobals, wrapActor, wrapCollisionData, SCRIPT_HOOKS } from './ScriptBridge.js';

/** Attaches a JavaScript file to an actor. */
export class ScriptComponent extends Component {
    static schema = {
        scriptPath: { type: P.Asset, assetKind: 'script', default: '' },
    };

    constructor() {
        super();
        this._scriptPath = '';
        this._hooks = {};
        this._functions = {};       // every top-level function, for invoke()
        this._pendingInvokes = [];  // invoke() calls made before the script loaded
        this._delegate = null;      // a Component instance, for module-style scripts
        this._started = false;
        this._loading = null;
        this._initialised = false;

        /** The last load error, surfaced by the editor's inspector. */
        this.error = null;
    }

    /**
     * The project-relative path of the script.
     *
     * Assigning re-initialises immediately, because every loader attaches the
     * component first and sets its properties second.
     */
    get scriptPath() { return this._scriptPath; }
    set scriptPath(value) {
        const next = value == null ? '' : String(value);
        if (this._scriptPath === next) return;
        this._scriptPath = next;
        if (this.actor) this._initialise();
    }

    awake() { if (this._scriptPath) this._initialise(); }

    start() {
        this._started = true;

        // The scene loader attaches components and applies their properties
        // before the actor joins a scene, so when `scriptPath` was set there was
        // no asset manager to load through and initialisation was skipped. By
        // `start` the actor is in the scene, which is the first moment the
        // script can actually be fetched.
        if (this._scriptPath && !this._initialised) {
            this._initialise();
            return;   // onStart fires when the load resolves
        }

        // A script still loading gets its `onStart` once the load resolves.
        if (this._loading) return;
        this._call('onStart');
    }

    update(dt) { this._call('onUpdate', dt); }
    fixedUpdate(dt) { this._call('onFixedUpdate', dt); }
    lateUpdate(dt) { this._call('onLateUpdate', dt); }

    // A flat-function script gets the same wrapped shapes it would under Jint; a
    // module-style Component subclass gets the engine's own objects.
    onCollisionEnter(data) { this._call('onCollisionEnter', this._delegate ? data : wrapCollisionData(data)); }
    onCollisionStay(data) { this._call('onCollisionStay', this._delegate ? data : wrapCollisionData(data)); }
    onCollisionExit(data) { this._call('onCollisionExit', this._delegate ? data : wrapCollisionData(data)); }
    onTriggerEnter(other) { this._call('onTriggerEnter', this._delegate ? other : wrapActor(other)); }
    onTriggerStay(other) { this._call('onTriggerStay', this._delegate ? other : wrapActor(other)); }
    onTriggerExit(other) { this._call('onTriggerExit', this._delegate ? other : wrapActor(other)); }

    onDestroy() {
        this._call('onDestroy');
        if (this._delegate) {
            this._delegate.onDestroy?.();
            this._delegate = null;
        }
        this._hooks = {};
        this._functions = {};
    }

    /**
     * Calls a top-level function the script defines and returns its result — what
     * another script reaches through `getComponent("ScriptComponent").invoke(name, ...args)`.
     * Returns undefined when there is no such function or it throws.
     */
    invoke(name, ...args) {
        // A script is fetched, so the spawner that just attached it and wants to
        // configure it — `script.invoke("configure", damage)` — is early. The call
        // waits and runs once the script is in, after onAwake and before onStart,
        // which is the order the C# engine gives the same code.
        if (!this._initialised || this._loading) {
            this._pendingInvokes.push({ name, args });
            return undefined;
        }

        const fn = this._functions[name] ?? this._hooks[name]
            ?? (typeof this._delegate?.[name] === 'function' ? this._delegate[name].bind(this._delegate) : null);
        if (!fn) return undefined;
        try {
            return fn(...args);
        } catch (err) {
            console.error(`[Script Error] ${this._scriptPath} (${name}): ${err.message}`);
            return undefined;
        }
    }

    /** The same as invoke(). */
    call(name, ...args) { return this.invoke(name, ...args); }

    /** Reloads the script from source. Module-level state is lost, by design. */
    reload() {
        this._hooks = {};
        this._functions = {};
        this._delegate = null;
        this._initialised = false;
        this._initialise();
    }

    _initialise() {
        this._hooks = {};
        this._functions = {};
        this._delegate = null;
        this.error = null;

        const assets = this.actor?.scene?.engine?.assets;
        if (!assets) {
            // No asset manager yet: either the actor is not in a scene, which
            // `start` retries, or there is no host at all, which is a test or a
            // headless tool feeding source in through `setSource`.
            return;
        }

        this._initialised = true;
        this._loading = assets.loadText(this._scriptPath)
            .then((source) => {
                this._loading = null;
                this._instantiate(source);
                this._call('onAwake');
                this._replayPendingInvokes();
                if (this._started) this._call('onStart');
            })
            .catch((err) => {
                this._loading = null;
                this.error = err.message;
                console.error(`[ScriptComponent] ${this._scriptPath}: ${err.message}`);
            });
    }

    /** Compiles the source and captures whichever hooks it defines. */
    _instantiate(source) {
        if (isModuleSource(source)) {
            this._instantiateModule(source);
            return;
        }
        this._instantiateFunctions(source);
    }

    _instantiateFunctions(source) {
        const globals = createScriptGlobals(this.actor);
        const names = Object.keys(globals);

        // Each script gets its own function scope, so two components running the
        // same file keep separate state — the isolation Jint gets from one engine
        // per component.
        //
        // Every top-level function is collected, not only the hooks, so another
        // script can reach `takeDamage` through invoke(). The scan is deliberately
        // loose — a nested helper, or the word in a comment, is caught too — because
        // a name that is not a function at the top level collects as null and is
        // dropped; `typeof` on an undeclared name is 'undefined', not an error.
        const declared = new Set(SCRIPT_HOOKS);
        for (const match of source.matchAll(/\bfunction\s+([A-Za-z_$][\w$]*)\s*\(/g)) {
            declared.add(match[1]);
        }
        const collect = [...declared]
            .map((name) => `${JSON.stringify(name)}: typeof ${name} === 'function' ? ${name} : null`)
            .join(',\n    ');

        const body = `${source}\n\nreturn {\n    ${collect}\n};`;

        try {
            // eslint-disable-next-line no-new-func
            const factory = new Function(...names, body);
            const found = factory(...names.map((n) => globals[n]));
            for (const [name, fn] of Object.entries(found)) {
                if (!fn) continue;
                if (SCRIPT_HOOKS.includes(name)) this._hooks[name] = fn;
                this._functions[name] = fn;
            }
        } catch (err) {
            this.error = err.message;
            console.error(`[ScriptComponent] ${this._scriptPath} failed to compile: ${err.message}`);
        }
    }

    _instantiateModule(source) {
        // Module scripts are loaded by the asset manager, which caches the
        // evaluated module; this path only wires the exported class to the actor.
        const exported = source.__module ?? null;
        if (!exported) {
            this.error = 'Module scripts must be loaded through AssetManager.loadModule().';
            return;
        }

        const Ctor = exported.default ?? Object.values(exported).find(
            (v) => typeof v === 'function' && v.prototype instanceof Component);

        if (!Ctor) {
            this.error = 'Module script exports no Component subclass.';
            return;
        }

        this._delegate = new Ctor();
        this._delegate.actor = this.actor;
        this._delegate.awake?.();

        // Forward the whole lifecycle to the delegate.
        for (const [hook, method] of Object.entries(MODULE_HOOK_MAP)) {
            if (typeof this._delegate[method] === 'function') {
                this._hooks[hook] = (...args) => this._delegate[method](...args);
            }
        }
    }

    _call(hook, ...args) {
        const fn = this._hooks[hook];
        if (!fn) return;
        try {
            fn(...args);
        } catch (err) {
            console.error(`[Script Error] ${this._scriptPath} (${hook}): ${err.message}`);
        }
    }

    /**
     * Runs a script from source text without loading a file. Used by tests and
     * by the editor's console, and by any host that already has the text.
     */
    setSource(source) {
        this._hooks = {};
        this._functions = {};
        this._delegate = null;
        this._initialised = true;
        this._instantiate(source);
        this._call('onAwake');
        this._replayPendingInvokes();
        if (this._started) this._call('onStart');
    }

    _replayPendingInvokes() {
        const pending = this._pendingInvokes;
        this._pendingInvokes = [];
        for (const { name, args } of pending) this.invoke(name, ...args);
    }

    /** The hook names the loaded script actually defines. */
    get definedHooks() { return Object.keys(this._hooks); }
}
registerComponent(ScriptComponent, {
    category: 'Scripting',
    summary: 'Runs a project JavaScript file against this actor.',
});

const MODULE_HOOK_MAP = {
    onAwake: 'awake',
    onStart: 'start',
    onUpdate: 'update',
    onFixedUpdate: 'fixedUpdate',
    onLateUpdate: 'lateUpdate',
    onDestroy: 'onDestroy',
    onCollisionEnter: 'onCollisionEnter',
    onCollisionStay: 'onCollisionStay',
    onCollisionExit: 'onCollisionExit',
    onTriggerEnter: 'onTriggerEnter',
    onTriggerStay: 'onTriggerStay',
    onTriggerExit: 'onTriggerExit',
};

/** True when the source looks like an ES module rather than a flat script. */
function isModuleSource(source) {
    if (typeof source !== 'string') return true;   // already an evaluated module
    return /^\s*(export|import)\s/m.test(source);
}
