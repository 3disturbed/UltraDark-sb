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
export { PhysicsSystem3D, RaycastHit3D } from './physics/PhysicsSystem3D.js';
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
export { createScriptGlobals, wrapActor, wrapComponent, SCRIPT_HOOKS } from './scripting/ScriptBridge.js';

// ---- Compatibility ----------------------------------------------------------
export { installCSharpAliases, aliasInstanceFields, areCSharpAliasesInstalled } from './compat/CSharpNaming.js';

/** The engine's version, reported by the runtime and the editor. */
export const VERSION = '1.0.0';
