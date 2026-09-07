// -----------------------------------------------------------------------------
// NetworkObject — per-actor network identity, ownership and state collection.
//
// The state blob this writes is byte-for-byte what
// SexyBiscuit.Engine/Networking/NetworkObject.cs writes, so a browser client and
// a native server replicate to each other without a translation layer:
//
//   [count u16][ member … ]      member = [name str][tag u8][value]
//
// What counts as replicated differs between the two engines only in how it is
// declared. C# marks a field with [Replicated]; here a component's own schema
// entry says `replicated: true`, which is the same statement in the idiom each
// language already uses for saveable state.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { PropertyType } from '../core/PropertyTypes.js';
import { Vector2, Vector3 } from '../math/index.js';
import { NetWriter, NetReader } from './protocol.js';
import { NetworkManager } from './NetworkManager.js';

/** Value type tags. The numbers match ValueTag in NetworkObject.cs. */
const Tag = {
    Bool: 1,
    Int32: 2,
    UInt32: 3,
    Float: 4,
    String: 5,
    Vector2: 6,
    Vector3: 7,
    Byte: 8,
    Int64: 9,
    Double: 10,
    Unknown: 255,
};

/** When a replicated member is sent. Mirrors ReplicateCondition in C#. */
export const ReplicateCondition = {
    Always: 'always',
    /** Only in the first snapshot — spawn-time facts that never change. */
    InitialOnly: 'initialOnly',
    /** Only to the peer that owns the object: its own hand of cards, its own aim. */
    OwnerOnly: 'ownerOnly',
};

/** Network identity for one actor. Every replicated actor needs one. */
export class NetworkObject extends Component {
    static schema = {
        ...Component.schema,
    };

    constructor() {
        super();
        /** Assigned by the server at spawn. Zero until then. */
        this.networkId = 0;
        /** True when this peer has authority over the object. */
        this.isOwner = false;
        /** True when this peer is the server. */
        this.isServer = false;
        /** The client that owns it, or -1 for server-owned. */
        this.ownerClientId = -1;

        this._lastSent = new Map();
        this._initialSendPending = true;
    }

    // -------------------------------------------------------------------------
    // Spawning
    // -------------------------------------------------------------------------

    /**
     * Server-side: gives an actor a network identity and tells every client to build
     * its counterpart.
     *
     * Clients construct the actor themselves in response to the `spawn` event — the
     * engine sends a name, not a prefab. That factory table is the one piece of
     * networking glue a game always has to write.
     */
    static spawn(actor, ownerClientId = -1) {
        const manager = NetworkManager.instance;
        if (!manager) throw new Error('NetworkManager is not running.');
        if (!manager.isServer) throw new Error('NetworkObject.spawn must be called on the server.');

        const netObject = actor.getComponent(NetworkObject) ?? actor.addComponent(NetworkObject);
        netObject.networkId = manager.allocateNetworkId();
        netObject.isServer = true;
        netObject.isOwner = true;
        netObject.ownerClientId = ownerClientId;

        manager.registerObject(netObject);
        manager.broadcastSpawn(netObject.networkId, actor.name, ownerClientId,
            netObject.collectLocalState(true));
        return netObject;
    }

    /** Server-side: tells every client to destroy the actor, then destroys it here. */
    static despawn(actor) {
        const manager = NetworkManager.instance;
        if (!manager) throw new Error('NetworkManager is not running.');
        if (!manager.isServer) throw new Error('NetworkObject.despawn must be called on the server.');

        const netObject = actor.getComponent(NetworkObject);
        if (!netObject) throw new Error(`Actor '${actor.name}' has no NetworkObject.`);

        manager.unregisterObject(netObject);
        manager.broadcastDespawn(netObject.networkId);
        actor.destroy();
    }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    /**
     * Everything replicated that has changed since the last call.
     *
     * @param {boolean} [forceAll] Send every member regardless — the spawn snapshot.
     * @returns {Uint8Array} The blob, or a bare zero count when nothing changed.
     */
    collectLocalState(forceAll = false) {
        const isFirstSend = this._initialSendPending;
        this._initialSendPending = false;

        const body = new NetWriter();
        let count = 0;

        for (const member of this._replicatedMembers()) {
            if (member.condition === ReplicateCondition.InitialOnly && !isFirstSend && !forceAll) continue;
            if (member.condition === ReplicateCondition.OwnerOnly && !this.isOwner && !this.isServer) continue;

            const value = member.target[member.name];
            const key = `${member.ownerName}.${member.name}`;

            if (!forceAll && !isFirstSend && this._lastSent.has(key)
                && valuesEqual(this._lastSent.get(key), value)) {
                continue;
            }

            body.string(member.name);
            writeValue(body, value, member.type);
            this._lastSent.set(key, copyValue(value));
            count++;
        }

        // The count is prepended rather than patched in place: NetWriter cannot seek by
        // design, because a format that needs random access to encode is one more thing
        // the two engines can disagree about.
        const payload = new NetWriter();
        payload.byte(count & 0xff).byte((count >> 8) & 0xff).raw(body.toBytes());
        return payload.toBytes();
    }

    /** Applies a blob another peer produced. */
    applyRemoteState(data) {
        if (!data || data.length === 0) return;

        const lookup = new Map();
        for (const member of this._replicatedMembers()) lookup.set(member.name, member);

        const reader = new NetReader(data);
        const count = reader.byte() | (reader.byte() << 8);

        for (let i = 0; i < count; i++) {
            const name = reader.string();
            const member = lookup.get(name);
            if (!member) {
                // A member this build does not have. The tag says how long the value is,
                // so the rest of the blob still decodes — which is what lets an older
                // client stay in a session with a newer server instead of dropping every
                // update.
                skipValue(reader);
                continue;
            }
            member.target[member.name] = readValue(reader);
        }
    }

    /**
     * Every replicated member on the actor and its components.
     *
     * Read fresh each time rather than cached: a component added at runtime has to
     * start replicating, and the alternative is a stale list that silently stops
     * sending half the object.
     */
    _replicatedMembers() {
        const members = [];
        const scan = (target, ownerName) => {
            const schema = target.constructor?.schema;
            if (!schema) return;
            for (const [name, descriptor] of Object.entries(schema)) {
                if (!descriptor?.replicated) continue;
                members.push({
                    name,
                    target,
                    ownerName,
                    type: descriptor.type,
                    condition: descriptor.condition ?? ReplicateCondition.Always,
                    priority: descriptor.priority ?? 0,
                });
            }
        };

        const actor = this.actor;
        if (!actor) return members;

        scan(actor, 'actor');
        for (const component of actor.getAllComponents()) scan(component, component.constructor.name);

        // High priority first, so a truncated send still carries what matters most.
        members.sort((a, b) => b.priority - a.priority);
        return members;
    }
}

// -----------------------------------------------------------------------------
// Values
// -----------------------------------------------------------------------------

function writeValue(writer, value, type) {
    if (value instanceof Vector2 || type === PropertyType.Vector2) {
        const v = value ?? new Vector2();
        writer.byte(Tag.Vector2).float(v.x).float(v.y);
        return;
    }
    if (value instanceof Vector3 || type === PropertyType.Vector3) {
        const v = value ?? new Vector3();
        writer.byte(Tag.Vector3).float(v.x).float(v.y).float(v.z);
        return;
    }

    switch (typeof value) {
        case 'boolean':
            writer.byte(Tag.Bool).bool(value);
            return;
        case 'string':
            writer.byte(Tag.String).string(value);
            return;
        case 'number':
            // An int-typed member goes as an Int32 so the native side reads an int; every
            // other number goes as a double, which is what a JavaScript number is.
            if (type === PropertyType.Int) writer.byte(Tag.Int32).int(value | 0);
            else writer.byte(Tag.Double).double(value);
            return;
        default:
            // Anything else: a tag and an empty block, so a reader can step over it.
            writer.byte(Tag.Unknown).bytes(new Uint8Array(0));
    }
}

function readValue(reader) {
    const tag = reader.byte();
    switch (tag) {
        case Tag.Bool: return reader.bool();
        case Tag.Byte: return reader.byte();
        case Tag.Int32: return reader.int();
        case Tag.UInt32: return reader.uint();
        case Tag.Int64: return reader.double();
        case Tag.Float: return reader.float();
        case Tag.Double: return reader.double();
        case Tag.String: return reader.string();
        case Tag.Vector2: return new Vector2(reader.float(), reader.float());
        case Tag.Vector3: return new Vector3(reader.float(), reader.float(), reader.float());
        default: reader.bytes(); return null;
    }
}

function skipValue(reader) {
    const tag = reader.byte();
    switch (tag) {
        case Tag.Bool: case Tag.Byte: reader.byte(); break;
        case Tag.Int32: reader.int(); break;
        case Tag.UInt32: reader.uint(); break;
        case Tag.Int64: case Tag.Double: reader.double(); break;
        case Tag.Float: reader.float(); break;
        case Tag.String: reader.string(); break;
        case Tag.Vector2: reader.float(); reader.float(); break;
        case Tag.Vector3: reader.float(); reader.float(); reader.float(); break;
        default: reader.bytes(); break;
    }
}

function valuesEqual(a, b) {
    if (a === b) return true;
    if (a instanceof Vector2 && b instanceof Vector2) return a.x === b.x && a.y === b.y;
    if (a instanceof Vector3 && b instanceof Vector3) return a.x === b.x && a.y === b.y && a.z === b.z;
    return false;
}

function copyValue(value) {
    if (value instanceof Vector2) return new Vector2(value.x, value.y);
    if (value instanceof Vector3) return new Vector3(value.x, value.y, value.z);
    return value;
}
