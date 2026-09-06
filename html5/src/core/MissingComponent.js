// -----------------------------------------------------------------------------
// MissingComponent / MissingActorClass — placeholders that stop a scene losing
// data when a type cannot be resolved.
//
// This matters far more in the browser than it does in C#. A shared project's
// behaviour may be written in C# and compiled into an assembly no web runtime
// can load; those component types will always be unresolvable here. Keeping the
// type name and its property JSON verbatim means the HTML5 editor can open, edit
// and save such a scene, and the C# engine still finds its components intact.
// -----------------------------------------------------------------------------

import { Component } from './Component.js';
import { registerComponent } from './TypeRegistry.js';

/** Holds the data of a component whose type could not be resolved. Does nothing. */
export class MissingComponent extends Component {
    static schema = {};

    constructor() {
        super();
        /** The `type` string exactly as the file had it. */
        this.typeName = '';
        /** The raw property bag, written back untouched on save. */
        this.properties = null;
    }

    toString() { return `MissingComponent(${this.typeName})`; }
}
registerComponent(MissingComponent, { category: 'Core', hidden: true });

/** Marks an actor whose `class` could not be resolved, so the name survives a save. */
export class MissingActorClass extends Component {
    static schema = {};

    constructor() {
        super();
        this.className = '';
        /** The subclass's own property bag, written back untouched. */
        this.properties = null;
    }

    toString() { return `MissingActorClass(${this.className})`; }
}
registerComponent(MissingActorClass, { category: 'Core', hidden: true });
