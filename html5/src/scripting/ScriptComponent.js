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
import { createScriptGlobals, SCRIPT_HOOKS } from './ScriptBridge.js';

/** Attaches a JavaScript file to an actor. */
export class ScriptComponent extends Component {
    static schema = {
        scriptPath: { type: P.Asset, assetKind: 'script', default: '' },
    };

    constructor() {
        super();
        this._scriptPath = '';
        this._hooks = {};
        this._delegate = null;      // a Component instance, for module-style scripts
        this._started = false;
        this._loading = null;

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
        // A script still loading gets its `onStart` once the load resolves.
        if (this._loading) return;
        this._call('onStart');
    }

    update(dt) { this._call('onUpdate', dt); }
    fixedUpdate(dt) { this._call('onFixedUpdate', dt); }
    lateUpdate(dt) { this._call('onLateUpdate', dt); }

    onCollisionEnter(data) { this._call('onCollisionEnter', data); }
    onCollisionStay(data) { this._call('onCollisionStay', data); }
    onCollisionExit(data) { this._call('onCollisionExit', data); }
    onTriggerEnter(other) { this._call('onTriggerEnter', other); }
    onTriggerStay(other) { this._call('onTriggerStay', other); }
    onTriggerExit(other) { this._call('onTriggerExit', other); }

    onDestroy() {
        this._call('onDestroy');
        if (this._delegate) {
            this._delegate.onDestroy?.();
            this._delegate = null;
        }
        this._hooks = {};
    }

    /** Reloads the script from source. Module-level state is lost, by design. */
    reload() {
        this._hooks = {};
        this._delegate = null;
        this._initialise();
    }

    _initialise() {
        this._hooks = {};
        this._delegate = null;
        this.error = null;

        const assets = this.actor?.scene?.engine?.assets;
        if (!assets) {
            // No host: a test or a headless tool. Nothing to load from.
            return;
        }

        this._loading = assets.loadText(this._scriptPath)
            .then((source) => {
                this._loading = null;
                this._instantiate(source);
                this._call('onAwake');
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
        const collect = SCRIPT_HOOKS
            .map((hook) => `${JSON.stringify(hook)}: typeof ${hook} === 'function' ? ${hook} : null`)
            .join(',\n    ');

        const body = `${source}\n\nreturn {\n    ${collect}\n};`;

        try {
            // eslint-disable-next-line no-new-func
            const factory = new Function(...names, body);
            const found = factory(...names.map((n) => globals[n]));
            for (const [hook, fn] of Object.entries(found)) {
                if (fn) this._hooks[hook] = fn;
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
        this._delegate = null;
        this._instantiate(source);
        this._call('onAwake');
        if (this._started) this._call('onStart');
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
