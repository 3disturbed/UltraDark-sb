// -----------------------------------------------------------------------------
// SexyBiscuit HTML5 — the public API.
//
// Import what you need from here; every class mirrors its C# counterpart, with
// JavaScript casing. `installCSharpAliases` from `compat/CSharpNaming.js` adds
// PascalCase aliases when you would rather transliterate C# than rewrite it.
// -----------------------------------------------------------------------------

// ---- Maths ------------------------------------------------------------------
export { Vector2, Vector3, Vector4, Quaternion, Matrix4, Color, SBMath, Rect, Bounds, DEG2RAD, RAD2DEG, EPSILON } from './math/index.js';

// ---- Core -------------------------------------------------------------------
export { Actor } from './core/Actor.js';
export { Component, CollisionData } from './core/Component.js';
export { Scene } from './core/Scene.js';
export { Layer } from './core/Layer.js';
export { Transform } from './core/Transform.js';
export { Transform3D } from './core/Transform3D.js';
export { SceneManager } from './core/SceneManager.js';
export { Time } from './core/Time.js';
export { SBEvent } from './core/SBEvent.js';
export { PlayMode } from './core/PlayMode.js';
export { ExceptionIsolation, isolate } from './core/ExceptionIsolation.js';
export { MissingComponent, MissingActorClass } from './core/MissingComponent.js';
export {
    CoroutineRunner, Coroutine, YieldInstruction,
    WaitForSeconds, WaitForSecondsRealtime, WaitUntil, WaitWhile, WaitForEndOfFrame,
} from './core/CoroutineRunner.js';
export { TimerManager, TimerHandle } from './core/TimerManager.js';
export { PropertyType, coerce, encode, defaultValue } from './core/PropertyTypes.js';
export {
    registerComponent, registerActor, resolveComponent, resolveActor,
    componentNameOf, listComponents, listActors, componentEntry, schemaOf,
    unregisterProjectTypes,
} from './core/TypeRegistry.js';

// ---- Engine -----------------------------------------------------------------
export { EngineHost, EngineConfig } from './EngineHost.js';

// ---- Networking ----
export {
    NetworkManager, NetTransportKind, NetworkObject, ReplicateCondition,
    NetMessage, NET_PROTOCOL_VERSION, NetDelivery,
    NetWriter, NetReader, NetProtocolError,
    LoopbackTransport, WebSocketTransport, NetPeerHandle,
    createRoom, readRoom, roomSocketUrl, roomCodeFromUrl,
    generateRoomCode, isRoomCode, ROOM_CODE_ALPHABET, ROOM_CODE_LENGTH,
} from './net/index.js';

// ---- Darks Games account and social ----
export {
    DarksGames, DarksGamesUser,
    loadDarksGamesSdks, loadScript, resetSdkCache, DG_ORIGIN,
} from './dg/index.js';

// ---- Scene ------------------------------------------------------------------
export {
    SceneSerializer, serialize, deserialize, buildActor, buildActorDto,
    buildSceneDto, collectProperties, applyProperties,
} from './scene/SceneSerializer.js';
export { ActorPreset, ActorPresets, findPreset, presetCategories } from './scene/ActorPresets.js';
export { createDefault2D, createDefault3D, createEmpty } from './scene/SceneTemplates.js';

// ---- Rendering --------------------------------------------------------------
export { SpriteBatch, SpriteSortMode, SpriteEffects } from './rendering/SpriteBatch.js';
export { Camera2D, CameraFollow } from './rendering/Camera2D.js';
export { Camera3D } from './rendering/Camera3D.js';
export { SpriteRenderer } from './rendering/SpriteRenderer.js';
export { MeshRenderer } from './rendering/MeshRenderer.js';
export { Material3D } from './rendering/Material3D.js';
export { Light3D, LightType, SkyLight } from './rendering/Light3D.js';
export { Skybox } from './rendering/Skybox.js';
export { RenderSystem3D, RenderStats } from './rendering/RenderSystem3D.js';
export {
    MeshPrimitive, Geometry, getPrimitive, getPrimitiveBounds,
    setTessellation, clearPrimitiveCache,
} from './rendering/PrimitiveMesh.js';

// ---- Physics ----------------------------------------------------------------
export { PhysicsSystem2D, RaycastHit2D } from './physics/PhysicsSystem2D.js';
export { Rigidbody2D, BodyType } from './physics/Rigidbody2D.js';
export {
    Collider2D, BoxCollider2D, CircleCollider2D, PolygonCollider2D, PhysicsMaterial2D,
} from './physics/Collider2D.js';
export { Circle, Polygon, collide, raycastShape, aabbOverlap } from './physics/Shapes2D.js';
export { PhysicsSystem3D, RaycastHit3D, rayVsBounds } from './physics/PhysicsSystem3D.js';
export { Rigidbody3D } from './physics/Rigidbody3D.js';
export {
    Collider3D, BoxCollider3D, SphereCollider3D, CapsuleCollider3D, MeshCollider3D,
} from './physics/Collider3D.js';
export { CharacterController3D } from './physics/CharacterController3D.js';

// ---- Input ------------------------------------------------------------------
export { InputManager } from './input/InputManager.js';
export { Keys, MouseButton, canonicalKey, normalizeKeyCode } from './input/Keys.js';
export { ActionMap, InputAction, InputBinding } from './input/ActionMap.js';
export { GamepadState } from './input/GamepadState.js';
export { TouchManager, TouchPoint, TouchPhase, VirtualJoystick } from './input/TouchState.js';

// ---- Gameplay ---------------------------------------------------------------
export { Pawn } from './gameplay/Pawn.js';
export { Controller } from './gameplay/Controller.js';
export { PlayerController } from './gameplay/PlayerController.js';
export { Character } from './gameplay/Character.js';
export { GameMode } from './gameplay/GameMode.js';
export { GameState, MatchState } from './gameplay/GameState.js';
export { PlayerState } from './gameplay/PlayerState.js';
export { PlayerStart } from './gameplay/PlayerStart.js';
export { GameInstance, Subsystem } from './gameplay/GameInstance.js';

// ---- Assets and audio -------------------------------------------------------
export { AssetManager } from './assets/AssetManager.js';
export { Texture2D } from './assets/Texture2D.js';
export { AudioManager, AudioBus, AudioHandle } from './audio/AudioManager.js';

// ---- Animation --------------------------------------------------------------
export { Tween, TweenSequence } from './animation/Tween.js';
export { Easing, getEasing, EASE_TYPES } from './animation/TweenEasing.js';

// ---- Scripting --------------------------------------------------------------
export { ScriptComponent } from './scripting/ScriptComponent.js';
// ---- MakeChibi ------------------------------------------------------------
export {
    ChibiParts, JOINTS, JOINT_NAMES, SOCKETS, SOCKET_NAMES, SLOTS, COLOUR_SLOTS,
    ROOT_NAME, variantsFor, accessoryNames, isMirrored, expand,
} from './chibi/ChibiRig.js';
export {
    defaultRecipe, normalise as normaliseRecipe, parse as parseRecipe,
    stringify as stringifyRecipe, random as randomRecipe, seededRandom, PROPORTIONS,
} from './chibi/ChibiRecipe.js';
export { build as buildChibi, ChibiBuild } from './chibi/ChibiBuilder.js';
export { Pose, PROCEDURAL, KEYED, findClip, clipNames } from './chibi/ChibiClips.js';
export { ChibiCharacter } from './chibi/ChibiCharacter.js';
export { ChibiAnimator } from './chibi/ChibiAnimator.js';

export { createScriptGlobals, wrapActor, unwrapActor, wrapCollisionData, wrapComponent, SCRIPT_HOOKS } from './scripting/ScriptBridge.js';

// ---- UI ---------------------------------------------------------------------
// The retained tree. ScriptUi is the flat five-kind surface the `UI` script global
// builds on top of; UiCanvas is the tree UiLayout, UiFocus and UiDocument work on.
export { ScriptUi, ANCHORS, widestLine } from './ui/ScriptUi.js';
export { UiCanvas } from './ui/UiCanvas.js';
export { UiNode } from './ui/UiNode.js';
export { UiFocus, UiInputModeTracker } from './ui/UiFocus.js';
export { UiInput, EMPTY_FRAME as UI_EMPTY_FRAME } from './ui/UiInput.js';
export { paint as paintUi, paintAll as paintUiCanvases } from './ui/UiPainter.js';
export {
    UiKind, SizeMode, LayoutMode, AlignMode, PositionMode, ScrollMode,
    UiAnchor, Focusability, UiInputMode, UiScaleMode, SafeAreaMode, UiDocumentError,
} from './ui/UiEnums.js';
export { fromJson as uiFromJson, fromObject as uiFromObject } from './ui/UiDocument.js';
export { NavDirection } from './ui/UiNavigation.js';

// ---- Graphics settings ------------------------------------------------------
export {
    GraphicsSettings, ShadowQuality, TextureFiltering, Lighting2DQuality,
    PREFS_KEY as GRAPHICS_PREFS_KEY, saveGraphicsSettings, loadGraphicsSettings, applyGlobals as applyGraphicsGlobals,
} from './rendering/GraphicsSettings.js';
export { GraphicsCapabilities } from './rendering/GraphicsCapabilities.js';
export { GraphicsBenchmark, summarise as summariseBenchmark, describe as describeBenchmark } from './debug/GraphicsBenchmark.js';
export { GraphicsMenu } from './ui/GraphicsMenu.js';

// ---- Day and night ----------------------------------------------------------
export { TimeOfDay, DayPhase, TWILIGHT_DEGREES } from './rendering/TimeOfDay.js';
export { sample as sampleSky, wrap01 as wrapDay, mixHex, KEY_NAMES as SKY_KEY_NAMES } from './rendering/SkyGradient.js';

// ---- Save -------------------------------------------------------------------
export { PlayerPrefs } from './save/PlayerPrefs.js';

// ---- Compatibility ----------------------------------------------------------
export { installCSharpAliases, aliasInstanceFields, areCSharpAliasesInstalled } from './compat/CSharpNaming.js';

// ---- Identity ---------------------------------------------------------------
// Re-exported rather than restated: EngineInfo.js is the one place the browser
// engine's version lives, and it is pinned equal to <Version> in the engine csproj.
export { VERSION, NAME as ENGINE_NAME, rendererName, platformName, statusLine } from './core/EngineInfo.js';
