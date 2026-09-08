// =============================================================================
// SexyBiscuit Engine — Script API Declarations
// Generated from html5/src/scripting/bridge-api.json by html5/tools/gen-dts.js.
// Do not edit: change the contract and run `npm run gen` in html5/.
//
// This surface is shared with the HTML5 runtime: a script that uses only what is
// declared here runs unchanged under both engines. Place this file beside your
// Scripts/ and reference it from jsconfig.json for completion in an editor.
// =============================================================================

/** Plain {x, y} value object used throughout the API. */
declare interface Vec2 {
    x: number;
    y: number;
}

/** Opaque handle returned by Audio.play(). Pass it to Audio.stop() to halt playback. */
declare interface AudioHandle {
    readonly id: number;
}

/**
 * A component reached through getComponent(). Every schema-declared property is readable and
 * writable under its C# name and its camelCase name ("GravityScale" or "gravityScale");
 * vectors read as {x, y} and colours as "#RRGGBBAA".
 */
declare interface ComponentProxy {
    readonly type: string;
    readonly actor: ActorProxy;
    enabled: boolean;
    [property: string]: any;
}

/** A ScriptComponent seen from another script. */
declare interface ScriptProxy extends ComponentProxy {
    /** Calls a top-level function the other script defines and returns its result. */
    invoke(name: string, ...args: any[]): any;
    /** The same as invoke(). */
    call(name: string, ...args: any[]): any;
}

/** An actor found through the Scene API, a hierarchy query or a collision. */
declare interface ActorProxy {
    readonly id: number;
    name: string;
    tag: string;
    active: boolean;
    /** Position and rotation of the found actor. */
    readonly transform: ActorProxyTransform;
    /** The actor's Transform3D, or null in a 2D scene. */
    readonly transform3d: Transform3DProxy | null;
    getComponent(type: string): ComponentProxy | null;
    destroy(): void;
}

/** The transform of a found actor: position and rotation only. */
declare interface ActorProxyTransform {
    /** World-space X position. */
    x: number;
    /** World-space Y position. */
    y: number;
    /** World-space rotation in radians. */
    rotation: number;
}

/** Contact data handed to the collision hooks. tag, name and getComponent forward to `other`. */
declare interface CollisionData {
    other: ActorProxy;
    contactPoint: Vec2;
    normal: Vec2;
    relativeVelocity: number;
    readonly tag: string;
    readonly name: string;
    /** The same as other.getComponent(type). */
    getComponent(type: string): ComponentProxy | null;
}

/**
 * One node in the screen-space tree, as UI.build returns it. The members are generated from
 * UiDocument's own key list on both engines, so this and the contract cannot describe
 * different things.
 */
declare interface UiNode {
    name: string;
    kind: string;
    visible: boolean;
    interactive: boolean;
    order: number;
    style: string;
    width: number | string;
    height: number | string;
    minWidth: number;
    minHeight: number;
    maxWidth: number;
    maxHeight: number;
    grow: number;
    shrink: number;
    padding: number[];
    margin: number[];
    layout: string;
    gap: number[];
    wrap: boolean;
    mainAlign: string;
    crossAlign: string;
    columns: number;
    cellSize: number[];
    positioning: string;
    anchor: string;
    anchorMin: number[];
    anchorMax: number[];
    pivot: number[];
    offset: number[];
    offsetMax: number[];
    text: string;
    textScale: number;
    textAlign: string;
    verticalAlign: string;
    wrapText: boolean;
    lineSpacing: number;
    background: string | null;
    tint: string;
    opacity: number;
    borderColour: string | null;
    borderWidth: number;
    texturePath: string;
    sourceRect: number[];
    ninePatch: number[];
    clip: boolean;
    scroll: string;
    scrollOffset: number[];
    ignoreSafeArea: boolean;
    focusable: string;
    modal: boolean;
    navUp: string;
    navDown: string;
    navLeft: string;
    navRight: string;
    autoFocus: boolean;
    value: number;
    minValue: number;
    maxValue: number;
    step: number;
    checked: boolean;
    selectedIndex: number;
    options: string[];
    x: number;
    y: number;
    scale: number;
    align: string;
    readonly rect: { x: number; y: number; width: number; height: number };
    readonly hovered: boolean;
    readonly pressed: boolean;
    readonly clicked: boolean;
    readonly focused: boolean;
    readonly expanded: boolean;
    /** The node this one hangs under, or null at the root. */
    readonly parent: UiNode | null;
    /** A fresh array each read, so a script cannot mutate the tree by writing to it. */
    readonly children: UiNode[];
    /** Appends a child built from a spec and returns it. */
    add(spec: object): UiNode;
    /** The first descendant with this name, or null. */
    find(name: string): UiNode | null;
    /** Detaches this node and everything under it. */
    remove(): void;
    /** Gives this node the focus a pad or a remote drives. */
    focus(): void;
}

// -----------------------------------------------------------------------------
// actor — The Actor that owns this script component.
// -----------------------------------------------------------------------------
declare const actor: {
    /** Stable numeric id, unique within the running scene. */
    readonly id: number;
    /** Display name of the actor (editable). */
    name: string;
    /** Tag string used for collision filtering and scene queries. */
    tag: string;
    /** Whether this actor is active and updated each frame. */
    active: boolean;
    /** The same object as the `transform` global. */
    readonly transform: typeof transform;
    /** The actor's Transform3D, or null in a 2D scene. */
    readonly transform3d: Transform3DProxy | null;
    /** The first component of the named type, or null. */
    getComponent(type: string): ComponentProxy | null;
    /** Adds a component by type name and returns its proxy. */
    addComponent(type: string): ComponentProxy | null;
    /** Remove this actor from the scene at the end of the current frame. */
    destroy(): void;
    /** The actor this one is attached to, or null at the root. */
    readonly parent: ActorProxy | null;
    /** A fresh array of the actors attached to this one. */
    readonly children: ActorProxy[];
    /**
     * Parents this actor to another; null detaches. keep (default true) holds the world
     * position, so a pickup follows the player from where it is rather than snapping to the
     * player's origin.
     */
    attachTo(other: ActorProxy | null, keep?: boolean): void;
    /** Removes this actor from its parent, keeping its world position by default. */
    detach(keep?: boolean): void;
    /** The first child with this name, or null; recursive searches the whole subtree. */
    findChild(name: string, recursive?: boolean): ActorProxy | null;
};

// -----------------------------------------------------------------------------
// transform — The actor's Transform component.
// -----------------------------------------------------------------------------
declare const transform: {
    /** World-space X position. */
    x: number;
    /** World-space Y position. */
    y: number;
    /** World-space rotation in radians. */
    rotation: number;
    /** World-space horizontal scale. */
    scaleX: number;
    /** World-space vertical scale. */
    scaleY: number;
    /** Rotates the actor so it faces the point (x, y) in world space. */
    lookAt(x: number, y: number): void;
    /** The distance from this transform to another {x, y}. */
    distanceTo(other: { x: number; y: number }): number;
};

// -----------------------------------------------------------------------------
// transform3d — The actor's Transform3D, or null in a 2D scene. Rotations are Euler angles in degrees.
// -----------------------------------------------------------------------------
/** The actor's Transform3D, or null in a 2D scene. Rotations are Euler angles in degrees. */
declare interface Transform3DProxy {
    x: number;
    y: number;
    z: number;
    rotX: number;
    rotY: number;
    rotZ: number;
    lookAt(x: number, y: number, z: number): void;
    /**
     * Local scale on X. Every mesh primitive is unit-sized, so scale is how a cube becomes a
     * wall.
     */
    scaleX: number;
    /** Local scale on Y. */
    scaleY: number;
    /** Local scale on Z. */
    scaleZ: number;
    /** Position and scale in one call: one matrix rebuild instead of six. */
    set(x: number, y: number, z: number, scaleX?: number, scaleY?: number, scaleZ?: number): void;
}

declare const transform3d: Transform3DProxy | null;

// -----------------------------------------------------------------------------
// Input — Input queries: actions, keys, mouse, touch and the on-screen joystick.
// -----------------------------------------------------------------------------
declare const Input: {
    /** True on the frame the named action becomes active. */
    isPressed(action: string): boolean;
    /** True while the named action is active. */
    isHeld(action: string): boolean;
    /** True on the frame the named action stops being active. */
    isReleased(action: string): boolean;
    /** The axis value for the named action, -1..1. */
    getAxis(action: string): number;
    /** Mouse cursor X in screen pixels. */
    readonly mouseX: number;
    /** Mouse cursor Y in screen pixels. */
    readonly mouseY: number;
    /** Mouse movement since the last frame. */
    readonly mouseDeltaX: number;
    readonly mouseDeltaY: number;
    /** Scroll wheel movement since the last frame, in notches. */
    readonly scrollDelta: number;
    /** Keys by name: "A", "a", "KeyA", "Space", "ArrowLeft" and "Left" all work. */
    isKeyDown(key: string): boolean;
    isKeyHeld(key: string): boolean;
    isKeyPressed(key: string): boolean;
    isKeyReleased(key: string): boolean;
    /** Mouse buttons: 0 left, 1 middle, 2 right, or "Left" / "Middle" / "Right". */
    isMouseDown(button: number | string): boolean;
    isMouseHeld(button: number | string): boolean;
    isMousePressed(button: number | string): boolean;
    isMouseReleased(button: number | string): boolean;
    /** Active touches. */
    readonly touchCount: number;
    getTouch(index: number): { id: number; x: number; y: number; phase: string } | null;
    /** The left on-screen joystick, -1..1 on each axis. */
    readonly joystickX: number;
    readonly joystickY: number;
};

// -----------------------------------------------------------------------------
// Audio — Audio playback.
// -----------------------------------------------------------------------------
declare const Audio: {
    /** Starts playback and returns a handle; id is 0 when nothing could play. */
    play(path: string, loop?: boolean): AudioHandle;
    /** Plays the file once, fire-and-forget. */
    playOneShot(path: string, volume?: number): void;
    /** Stops the voice behind a handle (or a raw id). */
    stop(handle: AudioHandle | number): void;
    /** Sets the master volume, 0..1. */
    setVolume(volume: number): void;
};

// -----------------------------------------------------------------------------
// Scene — Scene and actor management.
// -----------------------------------------------------------------------------
declare const Scene: {
    /** The name of the scene this actor is in. */
    readonly name: string;
    /** The first actor with the given name, or null. */
    find(name: string): ActorProxy | null;
    /** Every actor with the given name. */
    findAll(name: string): ActorProxy[];
    /** Every actor with the given tag. An empty array is still truthy. */
    findByTag(tag: string): ActorProxy[];
    /** The first actor with the given tag, or null. */
    findFirstByTag(tag: string): ActorProxy | null;
    /** Creates an empty actor in this scene at (x, y). */
    createActor(name: string, x?: number, y?: number): ActorProxy;
    /** Adds a component to an actor by type name, applying {Property: value} pairs. */
    addComponent(actor: ActorProxy, type: string, properties?: Record<string, any>): ComponentProxy | null;
    /** Removes an actor at the end of the frame. */
    destroy(actor: ActorProxy): void;
    /** The same as destroy(). */
    destroyActor(actor: ActorProxy): void;
    /** Instantiates a prefab file at (x, y), or an empty actor named after the path. */
    instantiate(prefab: string, x?: number, y?: number): ActorProxy;
    /** Requests a scene transition at the end of the frame. */
    load(scene: string): void;
};

// -----------------------------------------------------------------------------
// Debug — Logging.
// -----------------------------------------------------------------------------
declare const Debug: {
    /** Writes an informational message. Arguments are joined with spaces. */
    log(...args: any[]): void;
    /** Writes a warning. */
    warn(...args: any[]): void;
    /** Writes an error. */
    error(...args: any[]): void;
};

// -----------------------------------------------------------------------------
// Vector2 — 2D vector maths over plain {x, y} objects.
// -----------------------------------------------------------------------------
declare const Vector2: {
    create(x: number, y: number): Vec2;
    add(a: Vec2, b: Vec2): Vec2;
    /** a - b */
    sub(a: Vec2, b: Vec2): Vec2;
    scale(v: Vec2, s: number): Vec2;
    /** The unit vector, or {x: 0, y: 0} for a zero vector. */
    normalize(v: Vec2): Vec2;
    dot(a: Vec2, b: Vec2): number;
    distance(a: Vec2, b: Vec2): number;
    length(v: Vec2): number;
};

// -----------------------------------------------------------------------------
// Physics — 2D world queries.
// -----------------------------------------------------------------------------
declare const Physics: {
    /** The closest hit along a ray, or null. */
    raycast(originX: number, originY: number, dirX: number, dirY: number, maxDistance?: number): { actor: ActorProxy; x: number; y: number; normalX: number; normalY: number; distance: number } | null;
    /** Every actor whose collider overlaps the circle. */
    overlapCircle(x: number, y: number, radius: number): ActorProxy[];
    /** Every actor whose collider overlaps the axis-aligned box. */
    overlapBox(x: number, y: number, width: number, height: number): ActorProxy[];
};

// -----------------------------------------------------------------------------
// Time — Frame timing.
// -----------------------------------------------------------------------------
declare const Time: {
    readonly deltaTime: number;
    readonly unscaledDeltaTime: number;
    /** Scaled seconds since the engine started. */
    readonly time: number;
    readonly frameCount: number;
    readonly fps: number;
    /** 0 pauses gameplay, 0.5 is slow motion, 2 is double speed. */
    timeScale: number;
};

// -----------------------------------------------------------------------------
// Network — The multiplayer session. Present in every build so a multiplayer script runs solo; a solo session satisfies every query.
// -----------------------------------------------------------------------------
declare const Network: {
    readonly localId: number;
    readonly isServer: boolean;
    readonly isConnected: boolean;
    isLocalPlayer(id: number): boolean;
    startServer(port?: number): boolean;
    /** Joins a server by address and port, or a relay by ws:// URL and room code. */
    connect(address?: string, portOrRoom?: number | string): boolean;
    sendToAll(type: string, data?: any): void;
    broadcast(type: string, data?: any): void;
    /** True when this session hosts the game: the server, or the solo player. */
    readonly isHost: boolean;
    /** Round-trip time to the server in milliseconds; 0 when not connected. */
    readonly ping: number;
    /** The room code of a session joined through a relay, or an empty string. */
    readonly room: string;
    /** The name other players see. Set it before connecting. */
    playerName: string;
    /** A fresh array of the players in the session. */
    readonly players: { id: number; name: string }[];
    /**
     * Starts a single-player session that satisfies isHost and isConnected, so a multiplayer
     * script runs alone.
     */
    startSolo(): boolean;
    /** Leaves the session. */
    disconnect(): void;
    /** Sends a message to one player. */
    sendTo(clientId: number, type: string, data?: any): void;
    /**
     * Subscribes to a session event and returns the function that unsubscribes. Needs a
     * running session; the onNetworkMessage hook waits for one instead.
     */
    on(event: "message" | "playerJoined" | "playerLeft" | "connected" | "disconnected", handler: (...args: any[]) => void): () => void;
};

// -----------------------------------------------------------------------------
// UI — Screen space: the only global that knows how big the window is, and the tree a script builds in it.
// -----------------------------------------------------------------------------
declare const UI: {
    /** The viewport in canvas units. */
    readonly width: number;
    readonly height: number;
    /** What a notch or a television's overscan leaves usable. */
    readonly safeLeft: number;
    readonly safeTop: number;
    readonly safeRight: number;
    readonly safeBottom: number;
    /** Builds a whole tree in one call and returns its root. Building again replaces it. */
    build(spec: object): UiNode;
    /** This script's root node. */
    readonly root: UiNode;
    /** The first node with this name, or null. */
    find(name: string): UiNode | null;
    /** Drops this script's nodes. Another script's HUD is untouched. */
    clear(): void;
    /** The width one line of text will occupy. */
    measure(text: string, scale?: number): number;
    /** Paint order against the canvases other scripts own. Higher is in front. */
    order: number;
    /** The node holding focus, or null. */
    readonly focused: UiNode | null;
    /** Moves focus to a node, or to the node with that name. */
    setFocus(node: UiNode | string | null): boolean;
    /** Steps focus one place. Returns whether anything moved. */
    navigate(direction: "up" | "down" | "left" | "right"): boolean;
    /** Which class of device the player is driving with. */
    readonly inputMode: "pointer" | "directional" | "touch";
};

// -----------------------------------------------------------------------------
// DG — Darks Games accounts and social: identity, presence, achievements and cloud saves. Every member is safe to call in a build without the layer.
// -----------------------------------------------------------------------------
declare const DG: {
    /**
     * True when the build carries the Darks Games account layer. Every DG member is safe to
     * call without it.
     */
    readonly available: boolean;
    /** True once the player has signed in. */
    readonly signedIn: boolean;
    /** The account id, or null when signed out. */
    readonly userId: string | null;
    /** The account name, or null when signed out. */
    readonly userName: string | null;
    /** The public handle, or null when signed out. */
    readonly handle: string | null;
    /** The name to show; "Player" when signed out. */
    readonly displayName: string;
    /** The catalogue slug this build was exported with, or an empty string. */
    readonly game: string;
    /** Publishes what the player is doing, e.g. { state: 'lobby', joinCode: room }. */
    presence(fields: object): void;
    /** Clears the published presence. */
    clearPresence(): void;
    /** Reports an achievement, or adds to a counted one. */
    achievement(key: string, increment?: number): void;
    /**
     * Requests the cloud save. It arrives on the "save" event rather than as a return value,
     * because the Jint bridge cannot await.
     */
    loadSave(): void;
    /** Writes the cloud save. */
    saveCloud(data: any, version?: number): void;
    /**
     * Subscribes to an account event and returns the function that unsubscribes. "user"
     * reports (signedIn, id, displayName) on both engines.
     */
    on(event: "user" | "save" | "saveConflict" | "achievement", handler: (...args: any[]) => void): () => void;
};

// -----------------------------------------------------------------------------
// Chibi — MakeChibi's characters: spawning, dressing and animating them.
// -----------------------------------------------------------------------------
declare const Chibi: {
    /**
     * Spawns a character from a `.chibi` recipe under Assets/. The recipe is read from disk,
     * so the body appears a frame or two later; the actor itself is usable at once.
     */
    spawn(recipePath: string, x?: number, y?: number, z?: number): ActorProxy | null;
    /** A coordinated random character. The same seed is always the same character. */
    random(seed: number, x?: number, y?: number, z?: number): ActorProxy | null;
    /**
     * Plays a clip, cross-fading over `blend` seconds. Locomotion: "idle", "walk", "run".
     * Keyed: "wave", "hit", "jump", "cheer", "sit", "die". False for a name that is neither.
     */
    play(chibi: ActorProxy, clip: string, blend?: number): boolean;
    /** Stops whatever is playing, returning the character to rest. */
    stop(chibi: ActorProxy): void;
    /** Repaints one colour slot: skin, hair, eyes, top, bottom, shoes, accent. */
    setColour(chibi: ActorProxy, slot: string, hex: string): boolean;
    /** Swaps one style slot — head, hair, eyes, body, legs, feet — and rebuilds. */
    setStyle(chibi: ActorProxy, slot: string, variant: string): boolean;
    /** Hangs an actor off a socket: Head, Face, Hand_L, Hand_R, Back. */
    attach(chibi: ActorProxy, socket: string, actor: ActorProxy): boolean;
    /** The socket's own actor, for reading where it is. Null for an unknown name. */
    socket(chibi: ActorProxy, socket: string): ActorProxy | null;
};

// -----------------------------------------------------------------------------
// Bare functions
// -----------------------------------------------------------------------------
declare function log(...args: any[]): void;
declare function warn(...args: any[]): void;
declare function error(...args: any[]): void;

// -----------------------------------------------------------------------------
// Lifecycle hooks — define the ones a script needs
// -----------------------------------------------------------------------------
/** Called once when the script loads, after the actor has joined its scene. */
declare function onAwake(): void;
/** Called once on the first frame after the scene has started. */
declare function onStart(): void;
/** Called every frame. `dt` is seconds since the last frame. */
declare function onUpdate(dt: number): void;
/** Called at a fixed rate regardless of frame rate. Use for physics-dependent logic. */
declare function onFixedUpdate(dt: number): void;
/** Called every frame after every onUpdate has run. */
declare function onLateUpdate(dt: number): void;
/** Called just before the actor is destroyed or the scene unloads. */
declare function onDestroy(): void;
/** Called when this actor's collider first contacts another collider. */
declare function onCollisionEnter(data: CollisionData): void;
/** Called every physics step while the contact persists. */
declare function onCollisionStay(data: CollisionData): void;
/** Called when the contact ends. */
declare function onCollisionExit(data: CollisionData): void;
/** Called when another collider enters this actor's trigger volume. */
declare function onTriggerEnter(other: ActorProxy): void;
/** Called every physics step while it stays inside. */
declare function onTriggerStay(other: ActorProxy): void;
/** Called when it leaves. */
declare function onTriggerExit(other: ActorProxy): void;
/**
 * Called for every message the session delivers. Defining it is enough: the script attaches
 * itself the frame a session appears.
 */
declare function onNetworkMessage(type: string, payload: any, sender: number): void;
