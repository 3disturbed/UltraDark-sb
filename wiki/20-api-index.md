# 20. API Index

Every `public` type in `SexyBiscuit.Engine`, grouped by namespace, with a link to
its source file. Generated from the source tree; use it to find the file that
defines something, then read the XML doc comments there — they are consistently
good.

For the "how do I use this" version, follow the per-system pages linked from the
[wiki index](README.md).

---

## Quick lookup — the types you will use most

| I want to… | Type | Page |
|---|---|---|
| Bootstrap a game | `SBEngine`, `EngineConfig` | [1](01-getting-started.md), [3](03-game-loop.md) |
| Create objects | `Actor`, `Component`, `Transform` | [2](02-core-architecture.md) |
| Organise a scene | `Scene`, `Layer`, `SceneManager` | [2](02-core-architecture.md) |
| Draw a sprite | `SpriteRenderer`, `RenderSystem2D`, `Camera2D` | [4](04-rendering-2d.md) |
| Draw a mesh | `MeshRenderer`, `Camera3D`, `Material3D`, `Light3D` | [5](05-rendering-3d.md) |
| Add physics | `Rigidbody2D`, `BoxCollider2D`, `PhysicsSystem2D` | [6](06-physics.md) |
| Read input | `InputManager`, `ActionMap`, `InputBinding` | [7](07-input.md) |
| Play sound | `AudioManager`, `AudioSource`, `AudioBus` | [8](08-audio.md) |
| Build a HUD | `Canvas`, `Label`, `Button`, `Panel` | [9](09-ui.md) |
| Animate | `SpriteAnimator`, `Tween`, `AnimatorController` | [10](10-animation.md) |
| Script gameplay | `ScriptComponent`, `JintRuntime`, `ScriptBridge` | [11](11-scripting.md) |
| Load a scene file | `SceneSerializer`, `Prefab` | [12](12-scenes-prefabs.md) |
| Load assets | `AssetManager`, `AssetBundle` | [13](13-assets.md) |
| Save progress | `SaveManager`, `PlayerPrefs` | [14](14-save-system.md) |
| Go multiplayer | `NetworkManager`, `NetworkObject`, `RpcSystem` | [15](15-networking.md) |
| Structure a match | `GameInstance`, `GameMode`, `Pawn`, `Character` | [22](22-gameplay-framework.md) |
| Write enemy AI | `AIController`, `BehaviorTree`, `NavMesh` | [23](23-ai.md) |
| Translate text | `Loc` | [24](24-localization.md) |
| Pool actors | `ObjectPool<T>`, `ActorPool` | [24](24-localization.md) |
| Time things | `Time`, `TimerManager`, `CoroutineRunner` | [3](03-game-loop.md) |
| Do maths | `SBMath`, `Bounds`, `Easing` | [24](24-localization.md) |
| Debug | `Gizmos`, `DebugOverlay`, `Profiler` | [17](17-debugging.md) |
| Ship | `PlatformConfig`, `ExportPipeline`, `AssetCooker` | [18](18-build-export.md) |
| Steam | `SteamManager`, `SteamAchievements`, `SteamLobby` | [19](19-steam.md) |

---

## Full type index

### `SexyBiscuit.Engine`

| Type | Kind | File |
|---|---|---|
| `EngineConfig` | class | [`Engine.cs`](../SexyBiscuit.Engine/Engine.cs) |
| `SBEngine` | class | [`Engine.cs`](../SexyBiscuit.Engine/Engine.cs) |

### `SexyBiscuit.Engine.Core`

| Type | Kind | File |
|---|---|---|
| `Actor` | class | [`Actor.cs`](../SexyBiscuit.Engine/Core/Actor.cs) |
| `ActorPool` | class | [`ObjectPool.cs`](../SexyBiscuit.Engine/Core/ObjectPool.cs) |
| `Bounds` | struct | [`Bounds.cs`](../SexyBiscuit.Engine/Core/Bounds.cs) |
| `CollisionData` | struct | [`Component.cs`](../SexyBiscuit.Engine/Core/Component.cs) |
| `Component` | class | [`Component.cs`](../SexyBiscuit.Engine/Core/Component.cs) |
| `Coroutine` | class | [`CoroutineRunner.cs`](../SexyBiscuit.Engine/Core/CoroutineRunner.cs) |
| `CoroutineRunner` | class | [`CoroutineRunner.cs`](../SexyBiscuit.Engine/Core/CoroutineRunner.cs) |
| `Layer` | class | [`Layer.cs`](../SexyBiscuit.Engine/Core/Layer.cs) |
| `ObjectPool` | class | [`ObjectPool.cs`](../SexyBiscuit.Engine/Core/ObjectPool.cs) |
| `RequireComponentAttribute` | class | [`Component.cs`](../SexyBiscuit.Engine/Core/Component.cs) |
| `SBEvent` | class | [`SBEvent.cs`](../SexyBiscuit.Engine/Core/SBEvent.cs) |
| `SBMath` | class | [`SBMath.cs`](../SexyBiscuit.Engine/Core/SBMath.cs) |
| `Scene` | class | [`Scene.cs`](../SexyBiscuit.Engine/Core/Scene.cs) |
| `SceneManager` | class | [`SceneManager.cs`](../SexyBiscuit.Engine/Core/SceneManager.cs) |
| `Time` | class | [`Time.cs`](../SexyBiscuit.Engine/Core/Time.cs) |
| `TimerHandle` | struct | [`TimerManager.cs`](../SexyBiscuit.Engine/Core/TimerManager.cs) |
| `TimerManager` | class | [`TimerManager.cs`](../SexyBiscuit.Engine/Core/TimerManager.cs) |
| `Transform` | class | [`Transform.cs`](../SexyBiscuit.Engine/Core/Transform.cs) |
| `Transform3D` | class | [`Transform3D.cs`](../SexyBiscuit.Engine/Core/Transform3D.cs) |
| `WaitForSeconds` | class | [`CoroutineRunner.cs`](../SexyBiscuit.Engine/Core/CoroutineRunner.cs) |
| `WaitForSecondsRealtime` | class | [`CoroutineRunner.cs`](../SexyBiscuit.Engine/Core/CoroutineRunner.cs) |
| `WaitUntil` | class | [`CoroutineRunner.cs`](../SexyBiscuit.Engine/Core/CoroutineRunner.cs) |
| `WaitWhile` | class | [`CoroutineRunner.cs`](../SexyBiscuit.Engine/Core/CoroutineRunner.cs) |
| `YieldInstruction` | class | [`CoroutineRunner.cs`](../SexyBiscuit.Engine/Core/CoroutineRunner.cs) |

### `SexyBiscuit.Engine.Gameplay`

| Type | Kind | File |
|---|---|---|
| `Character` | class | [`Character.cs`](../SexyBiscuit.Engine/Gameplay/Character.cs) |
| `Controller` | class | [`Controller.cs`](../SexyBiscuit.Engine/Gameplay/Controller.cs) |
| `GameInstance` | class | [`GameInstance.cs`](../SexyBiscuit.Engine/Gameplay/GameInstance.cs) |
| `GameInstanceSubsystem` | class | [`Subsystem.cs`](../SexyBiscuit.Engine/Gameplay/Subsystem.cs) |
| `GameMode` | class | [`GameMode.cs`](../SexyBiscuit.Engine/Gameplay/GameMode.cs) |
| `GameState` | class | [`GameState.cs`](../SexyBiscuit.Engine/Gameplay/GameState.cs) |
| `MatchState` | enum | [`GameState.cs`](../SexyBiscuit.Engine/Gameplay/GameState.cs) |
| `Pawn` | class | [`Pawn.cs`](../SexyBiscuit.Engine/Gameplay/Pawn.cs) |
| `PlayerController` | class | [`PlayerController.cs`](../SexyBiscuit.Engine/Gameplay/PlayerController.cs) |
| `PlayerStart` | class | [`GameMode.cs`](../SexyBiscuit.Engine/Gameplay/GameMode.cs) |
| `PlayerState` | class | [`PlayerState.cs`](../SexyBiscuit.Engine/Gameplay/PlayerState.cs) |
| `Subsystem` | class | [`Subsystem.cs`](../SexyBiscuit.Engine/Gameplay/Subsystem.cs) |
| `SubsystemCollection` | class | [`Subsystem.cs`](../SexyBiscuit.Engine/Gameplay/Subsystem.cs) |
| `WorldSubsystem` | class | [`Subsystem.cs`](../SexyBiscuit.Engine/Gameplay/Subsystem.cs) |

### `SexyBiscuit.Engine.Rendering`

| Type | Kind | File |
|---|---|---|
| `Camera2D` | class | [`Camera2D.cs`](../SexyBiscuit.Engine/Rendering/Camera2D.cs) |
| `Camera3D` | class | [`Camera3D.cs`](../SexyBiscuit.Engine/Rendering/Camera3D.cs) |
| `CameraControllerBase` | class | [`CameraControllers.cs`](../SexyBiscuit.Engine/Rendering/CameraControllers.cs) |
| `CameraShake` | class | [`CameraShake.cs`](../SexyBiscuit.Engine/Rendering/CameraShake.cs) |
| `EmitterShape3D` | enum | [`ParticleSystem3D.cs`](../SexyBiscuit.Engine/Rendering/ParticleSystem3D.cs) |
| `FirstPersonController` | class | [`CameraControllers.cs`](../SexyBiscuit.Engine/Rendering/CameraControllers.cs) |
| `FlyCamController` | class | [`CameraControllers.cs`](../SexyBiscuit.Engine/Rendering/CameraControllers.cs) |
| `LODGroup` | class | [`LODGroup.cs`](../SexyBiscuit.Engine/Rendering/LODGroup.cs) |
| `LODLevel` | class | [`LODGroup.cs`](../SexyBiscuit.Engine/Rendering/LODGroup.cs) |
| `Light2D` | class | [`Light2D.cs`](../SexyBiscuit.Engine/Rendering/Light2D.cs) |
| `Light2DType` | enum | [`Light2D.cs`](../SexyBiscuit.Engine/Rendering/Light2D.cs) |
| `Light3D` | class | [`Light3D.cs`](../SexyBiscuit.Engine/Rendering/Light3D.cs) |
| `LightType` | enum | [`Light3D.cs`](../SexyBiscuit.Engine/Rendering/Light3D.cs) |
| `Lighting2D` | class | [`Light2D.cs`](../SexyBiscuit.Engine/Rendering/Light2D.cs) |
| `Material3D` | class | [`Material3D.cs`](../SexyBiscuit.Engine/Rendering/Material3D.cs) |
| `MeshRenderer` | class | [`MeshRenderer.cs`](../SexyBiscuit.Engine/Rendering/MeshRenderer.cs) |
| `OrbitCamController` | class | [`CameraControllers.cs`](../SexyBiscuit.Engine/Rendering/CameraControllers.cs) |
| `Particle` | struct | [`ParticleEmitter.cs`](../SexyBiscuit.Engine/Rendering/ParticleEmitter.cs) |
| `ParticleEmitter` | class | [`ParticleEmitter.cs`](../SexyBiscuit.Engine/Rendering/ParticleEmitter.cs) |
| `ParticleRenderMode3D` | enum | [`ParticleSystem3D.cs`](../SexyBiscuit.Engine/Rendering/ParticleSystem3D.cs) |
| `ParticleSystem3D` | class | [`ParticleSystem3D.cs`](../SexyBiscuit.Engine/Rendering/ParticleSystem3D.cs) |
| `PostProcessPass` | class | [`RenderSystem2D.cs`](../SexyBiscuit.Engine/Rendering/RenderSystem2D.cs) |
| `PostProcessPass3D` | class | [`PostProcessing3D.cs`](../SexyBiscuit.Engine/Rendering/PostProcessing3D.cs) |
| `PostProcessing3D` | class | [`PostProcessing3D.cs`](../SexyBiscuit.Engine/Rendering/PostProcessing3D.cs) |
| `RenderStats` | struct | [`RenderSystem3D.cs`](../SexyBiscuit.Engine/Rendering/RenderSystem3D.cs) |
| `RenderSystem2D` | class | [`RenderSystem2D.cs`](../SexyBiscuit.Engine/Rendering/RenderSystem2D.cs) |
| `RenderSystem3D` | class | [`RenderSystem3D.cs`](../SexyBiscuit.Engine/Rendering/RenderSystem3D.cs) |
| `ShadowCaster2D` | class | [`Light2D.cs`](../SexyBiscuit.Engine/Rendering/Light2D.cs) |
| `SkinnedMeshRenderer` | class | [`SkinnedMeshRenderer.cs`](../SexyBiscuit.Engine/Rendering/SkinnedMeshRenderer.cs) |
| `Skybox` | class | [`Skybox.cs`](../SexyBiscuit.Engine/Rendering/Skybox.cs) |
| `SpriteRenderer` | class | [`SpriteRenderer.cs`](../SexyBiscuit.Engine/Rendering/SpriteRenderer.cs) |
| `TileLayer` | class | [`TilemapRenderer.cs`](../SexyBiscuit.Engine/Rendering/TilemapRenderer.cs) |
| `TilemapData` | class | [`TilemapRenderer.cs`](../SexyBiscuit.Engine/Rendering/TilemapRenderer.cs) |
| `TilemapRenderer` | class | [`TilemapRenderer.cs`](../SexyBiscuit.Engine/Rendering/TilemapRenderer.cs) |
| `VertexPositionNormalTextureBlend` | struct | [`SkinnedMeshRenderer.cs`](../SexyBiscuit.Engine/Rendering/SkinnedMeshRenderer.cs) |

### `SexyBiscuit.Engine.Physics`

| Type | Kind | File |
|---|---|---|
| `AngularWeldConstraint` | class | [`Constraints3D.cs`](../SexyBiscuit.Engine/Physics/Constraints3D.cs) |
| `BallSocketConstraint` | class | [`Constraints3D.cs`](../SexyBiscuit.Engine/Physics/Constraints3D.cs) |
| `BoxCollider2D` | class | [`Collider2D.cs`](../SexyBiscuit.Engine/Physics/Collider2D.cs) |
| `BoxCollider3D` | class | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `CapsuleCollider3D` | class | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `CharacterController2D` | class | [`CharacterController2D.cs`](../SexyBiscuit.Engine/Physics/CharacterController2D.cs) |
| `CharacterController3D` | class | [`CharacterController3D.cs`](../SexyBiscuit.Engine/Physics/CharacterController3D.cs) |
| `CircleCollider2D` | class | [`Collider2D.cs`](../SexyBiscuit.Engine/Physics/Collider2D.cs) |
| `Collider2D` | class | [`Collider2D.cs`](../SexyBiscuit.Engine/Physics/Collider2D.cs) |
| `Collider3D` | class | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `CompositeCollider2D` | class | [`Collider2D.cs`](../SexyBiscuit.Engine/Physics/Collider2D.cs) |
| `CompositeShape2D` | class | [`Collider2D.cs`](../SexyBiscuit.Engine/Physics/Collider2D.cs) |
| `CompoundChildKind` | enum | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `CompoundChildShape` | class | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `CompoundCollider3D` | class | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `Constraint3D` | class | [`Constraints3D.cs`](../SexyBiscuit.Engine/Physics/Constraints3D.cs) |
| `CylinderCollider3D` | class | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `DistanceLimitConstraint` | class | [`Constraints3D.cs`](../SexyBiscuit.Engine/Physics/Constraints3D.cs) |
| `EdgeCollider2D` | class | [`Collider2D.cs`](../SexyBiscuit.Engine/Physics/Collider2D.cs) |
| `HingeConstraint` | class | [`Constraints3D.cs`](../SexyBiscuit.Engine/Physics/Constraints3D.cs) |
| `MeshCollider3D` | class | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `PhysicsConvert` | class | [`PhysicsSystem2D.cs`](../SexyBiscuit.Engine/Physics/PhysicsSystem2D.cs) |
| `PhysicsConvert3D` | class | [`PhysicsSystem3D.cs`](../SexyBiscuit.Engine/Physics/PhysicsSystem3D.cs) |
| `PhysicsMaterial2D` | class | [`Collider2D.cs`](../SexyBiscuit.Engine/Physics/Collider2D.cs) |
| `PhysicsSystem2D` | class | [`PhysicsSystem2D.cs`](../SexyBiscuit.Engine/Physics/PhysicsSystem2D.cs) |
| `PhysicsSystem3D` | class | [`PhysicsSystem3D.cs`](../SexyBiscuit.Engine/Physics/PhysicsSystem3D.cs) |
| `PolygonCollider2D` | class | [`Collider2D.cs`](../SexyBiscuit.Engine/Physics/Collider2D.cs) |
| `RaycastHit2D` | struct | [`PhysicsSystem2D.cs`](../SexyBiscuit.Engine/Physics/PhysicsSystem2D.cs) |
| `RaycastHit3D` | struct | [`PhysicsSystem3D.cs`](../SexyBiscuit.Engine/Physics/PhysicsSystem3D.cs) |
| `Rigidbody2D` | class | [`Rigidbody2D.cs`](../SexyBiscuit.Engine/Physics/Rigidbody2D.cs) |
| `Rigidbody3D` | class | [`Rigidbody3D.cs`](../SexyBiscuit.Engine/Physics/Rigidbody3D.cs) |
| `SliderConstraint` | class | [`Constraints3D.cs`](../SexyBiscuit.Engine/Physics/Constraints3D.cs) |
| `SphereCollider3D` | class | [`Collider3D.cs`](../SexyBiscuit.Engine/Physics/Collider3D.cs) |
| `TilemapCollider2D` | class | [`TilemapCollider2D.cs`](../SexyBiscuit.Engine/Physics/TilemapCollider2D.cs) |

### `SexyBiscuit.Engine.Input`

| Type | Kind | File |
|---|---|---|
| `ActionMap` | class | [`ActionMap.cs`](../SexyBiscuit.Engine/Input/ActionMap.cs) |
| `GamepadState` | class | [`GamepadState.cs`](../SexyBiscuit.Engine/Input/GamepadState.cs) |
| `InputAction` | class | [`ActionMap.cs`](../SexyBiscuit.Engine/Input/ActionMap.cs) |
| `InputBinding` | record | [`ActionMap.cs`](../SexyBiscuit.Engine/Input/ActionMap.cs) |
| `InputManager` | class | [`InputManager.cs`](../SexyBiscuit.Engine/Input/InputManager.cs) |
| `MouseButton` | enum | [`MouseButton.cs`](../SexyBiscuit.Engine/Input/MouseButton.cs) |
| `TouchManager` | class | [`TouchState.cs`](../SexyBiscuit.Engine/Input/TouchState.cs) |
| `TouchPhase` | enum | [`TouchState.cs`](../SexyBiscuit.Engine/Input/TouchState.cs) |
| `TouchPoint` | record | [`TouchState.cs`](../SexyBiscuit.Engine/Input/TouchState.cs) |
| `VirtualJoystick` | class | [`TouchState.cs`](../SexyBiscuit.Engine/Input/TouchState.cs) |

### `SexyBiscuit.Engine.Audio`

| Type | Kind | File |
|---|---|---|
| `AudioBus` | class | [`AudioBus.cs`](../SexyBiscuit.Engine/Audio/AudioBus.cs) |
| `AudioEffectProcessor` | class | [`AudioEffect.cs`](../SexyBiscuit.Engine/Audio/AudioEffect.cs) |
| `AudioEffectSettings` | class | [`AudioEffect.cs`](../SexyBiscuit.Engine/Audio/AudioEffect.cs) |
| `AudioEffectType` | enum | [`AudioEffect.cs`](../SexyBiscuit.Engine/Audio/AudioEffect.cs) |
| `AudioHandle` | struct | [`AudioHandle.cs`](../SexyBiscuit.Engine/Audio/AudioHandle.cs) |
| `AudioManager` | class | [`AudioManager.cs`](../SexyBiscuit.Engine/Audio/AudioManager.cs) |
| `AudioSource` | class | [`AudioSource.cs`](../SexyBiscuit.Engine/Audio/AudioSource.cs) |

### `SexyBiscuit.Engine.UI`

| Type | Kind | File |
|---|---|---|
| `UiCanvas` | class | [`UiCanvas.cs`](../SexyBiscuit.Engine/UI/UiCanvas.cs) |
| `UiNode` | class | [`UiNode.cs`](../SexyBiscuit.Engine/UI/UiNode.cs) |
| `UiDocument` | static class | [`UiDocument.cs`](../SexyBiscuit.Engine/UI/UiDocument.cs) |
| `UiDocumentException` | class | [`UiDocument.cs`](../SexyBiscuit.Engine/UI/UiDocument.cs) |
| `UiWorld` | static class | [`UiWorld.cs`](../SexyBiscuit.Engine/UI/UiWorld.cs) |
| `UiPainter` | static class | [`UiPainter.cs`](../SexyBiscuit.Engine/UI/UiPainter.cs) |
| `UiInput` | class | [`UiInput.cs`](../SexyBiscuit.Engine/UI/UiInput.cs) |
| `UiInputFrame` | struct | [`UiInput.cs`](../SexyBiscuit.Engine/UI/UiInput.cs) |
| `UiTextMeasure` | static class | [`UiTextMeasure.cs`](../SexyBiscuit.Engine/UI/UiTextMeasure.cs) |
| `BitmapFont` | static class | [`BitmapFont.cs`](../SexyBiscuit.Engine/UI/BitmapFont.cs) |
| `RectangleF` | record struct | [`RectangleF.cs`](../SexyBiscuit.Engine/UI/RectangleF.cs) |
| `GraphicsMenu` | class | [`GraphicsMenu.cs`](../SexyBiscuit.Engine/UI/GraphicsMenu.cs) |
| `UiKind` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `SizeMode` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `LayoutMode` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `AlignMode` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `PositionMode` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `ScrollMode` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `UiAnchor` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `Focusability` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `UiInputMode` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `UiScaleMode` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |
| `SafeAreaMode` | enum | [`UiEnums.cs`](../SexyBiscuit.Engine/UI/UiEnums.cs) |

### `SexyBiscuit.Engine.UI.Layout`

| Type | Kind | File |
|---|---|---|
| `UiLayout` | static class | [`UiLayout.cs`](../SexyBiscuit.Engine/UI/Layout/UiLayout.cs) |

### `SexyBiscuit.Engine.UI` — focus and navigation

| Type | Kind | File |
|---|---|---|
| `UiFocus` | class | [`UiFocus.cs`](../SexyBiscuit.Engine/UI/Focus/UiFocus.cs) |
| `UiInputModeTracker` | class | [`UiFocus.cs`](../SexyBiscuit.Engine/UI/Focus/UiFocus.cs) |
| `UiNavigation` | static class | [`UiNavigation.cs`](../SexyBiscuit.Engine/UI/Focus/UiNavigation.cs) |
| `NavDirection` | enum | [`UiNavigation.cs`](../SexyBiscuit.Engine/UI/Focus/UiNavigation.cs) |
| `NavCandidate` | record struct | [`UiNavigation.cs`](../SexyBiscuit.Engine/UI/Focus/UiNavigation.cs) |
| `NavSettings` | record struct | [`UiNavigation.cs`](../SexyBiscuit.Engine/UI/Focus/UiNavigation.cs) |


### `SexyBiscuit.Engine.Animation`

| Type | Kind | File |
|---|---|---|
| `AnimationClip` | class | [`SpriteAnimator.cs`](../SexyBiscuit.Engine/Animation/SpriteAnimator.cs) |
| `AnimationKeyframe` | class | [`SkeletalAnimation.cs`](../SexyBiscuit.Engine/Animation/SkeletalAnimation.cs) |
| `AnimatorController` | class | [`AnimatorController.cs`](../SexyBiscuit.Engine/Animation/AnimatorController.cs) |
| `AnimatorParameter` | class | [`AnimatorController.cs`](../SexyBiscuit.Engine/Animation/AnimatorController.cs) |
| `AnimatorState` | class | [`AnimatorController.cs`](../SexyBiscuit.Engine/Animation/AnimatorController.cs) |
| `AnimatorTransition` | class | [`AnimatorController.cs`](../SexyBiscuit.Engine/Animation/AnimatorController.cs) |
| `Bone` | class | [`SkeletalAnimation.cs`](../SexyBiscuit.Engine/Animation/SkeletalAnimation.cs) |
| `BoneChannel` | class | [`SkeletalAnimation.cs`](../SexyBiscuit.Engine/Animation/SkeletalAnimation.cs) |
| `ConditionOperator` | enum | [`AnimatorController.cs`](../SexyBiscuit.Engine/Animation/AnimatorController.cs) |
| `EaseType` | enum | [`TweenEasing.cs`](../SexyBiscuit.Engine/Animation/TweenEasing.cs) |
| `Easing` | class | [`TweenEasing.cs`](../SexyBiscuit.Engine/Animation/TweenEasing.cs) |
| `ParameterType` | enum | [`AnimatorController.cs`](../SexyBiscuit.Engine/Animation/AnimatorController.cs) |
| `SkeletalAnimator` | class | [`SkeletalAnimation.cs`](../SexyBiscuit.Engine/Animation/SkeletalAnimation.cs) |
| `SkeletalClip` | class | [`SkeletalAnimation.cs`](../SexyBiscuit.Engine/Animation/SkeletalAnimation.cs) |
| `SpriteAnimator` | class | [`SpriteAnimator.cs`](../SexyBiscuit.Engine/Animation/SpriteAnimator.cs) |
| `TransitionCondition` | class | [`AnimatorController.cs`](../SexyBiscuit.Engine/Animation/AnimatorController.cs) |
| `Tween` | class | [`Tween.cs`](../SexyBiscuit.Engine/Animation/Tween.cs) |
| `TweenSequence` | class | [`Tween.cs`](../SexyBiscuit.Engine/Animation/Tween.cs) |

### `SexyBiscuit.Engine.AI`

| Type | Kind | File |
|---|---|---|
| `AIController` | class | [`AIController.cs`](../SexyBiscuit.Engine/AI/AIController.cs) |
| `ActionNode` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `BehaviorContext` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `BehaviorNode` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `BehaviorTree` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Blackboard` | class | [`Blackboard.cs`](../SexyBiscuit.Engine/AI/Blackboard.cs) |
| `CompositeNode` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Condition` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `ConditionNode` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Cooldown` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `DecoratorNode` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Inverter` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `MoveToNode` | class | [`AIController.cs`](../SexyBiscuit.Engine/AI/AIController.cs) |
| `NavMesh` | class | [`NavMesh.cs`](../SexyBiscuit.Engine/AI/NavMesh.cs) |
| `NavMeshAgent` | class | [`NavMeshAgent.cs`](../SexyBiscuit.Engine/AI/NavMeshAgent.cs) |
| `NodeStatus` | enum | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Parallel` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Repeater` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Selector` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Sequence` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `Succeeder` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |
| `WaitNode` | class | [`BehaviorTree.cs`](../SexyBiscuit.Engine/AI/BehaviorTree.cs) |

### `SexyBiscuit.Engine.Scripting`

| Type | Kind | File |
|---|---|---|
| `JintRuntime` | class | [`JintRuntime.cs`](../SexyBiscuit.Engine/Scripting/JintRuntime.cs) |
| `ScriptBridge` | class | [`ScriptBridge.cs`](../SexyBiscuit.Engine/Scripting/ScriptBridge.cs) |
| `ScriptComponent` | class | [`ScriptComponent.cs`](../SexyBiscuit.Engine/Scripting/ScriptComponent.cs) |
| `ScriptHotReload` | class | [`ScriptHotReload.cs`](../SexyBiscuit.Engine/Scripting/ScriptHotReload.cs) |
| `ScriptContract` | class | [`ScriptContract.cs`](../SexyBiscuit.Engine/Scripting/ScriptContract.cs) |
| `TypeScriptDefinitions` | class | [`TypeScriptDefinitions.cs`](../SexyBiscuit.Engine/Scripting/TypeScriptDefinitions.cs) |

### `SexyBiscuit.Engine.Scene`

| Type | Kind | File |
|---|---|---|
| `ColorJsonConverter` | class | [`SceneSerializer.cs`](../SexyBiscuit.Engine/Scene/SceneSerializer.cs) |
| `Prefab` | class | [`Prefab.cs`](../SexyBiscuit.Engine/Scene/Prefab.cs) |
| `QuaternionJsonConverter` | class | [`SceneSerializer.cs`](../SexyBiscuit.Engine/Scene/SceneSerializer.cs) |
| `SceneSerializer` | class | [`SceneSerializer.cs`](../SexyBiscuit.Engine/Scene/SceneSerializer.cs) |
| `Vector2JsonConverter` | class | [`SceneSerializer.cs`](../SexyBiscuit.Engine/Scene/SceneSerializer.cs) |
| `Vector3JsonConverter` | class | [`SceneSerializer.cs`](../SexyBiscuit.Engine/Scene/SceneSerializer.cs) |
| `Vector4JsonConverter` | class | [`SceneSerializer.cs`](../SexyBiscuit.Engine/Scene/SceneSerializer.cs) |
| `WorldStreamer` | class | [`WorldStreamer.cs`](../SexyBiscuit.Engine/Scene/WorldStreamer.cs) |

### `SexyBiscuit.Engine.Assets`

| Type | Kind | File |
|---|---|---|
| `AssetBundle` | class | [`AssetBundle.cs`](../SexyBiscuit.Engine/Assets/AssetBundle.cs) |
| `AssetManager` | class | [`AssetManager.cs`](../SexyBiscuit.Engine/Assets/AssetManager.cs) |
| `AssetRequest` | class | [`LoadingScreen.cs`](../SexyBiscuit.Engine/Assets/LoadingScreen.cs) |
| `LoadingScreen` | class | [`LoadingScreen.cs`](../SexyBiscuit.Engine/Assets/LoadingScreen.cs) |

### `SexyBiscuit.Engine.Save`

| Type | Kind | File |
|---|---|---|
| `IBinarySerializable` | interface | [`SaveManager.cs`](../SexyBiscuit.Engine/Save/SaveManager.cs) |
| `PlayerPrefs` | class | [`PlayerPrefs.cs`](../SexyBiscuit.Engine/Save/PlayerPrefs.cs) |
| `SaveManager` | class | [`SaveManager.cs`](../SexyBiscuit.Engine/Save/SaveManager.cs) |
| `SaveSlotInfo` | class | [`SaveManager.cs`](../SexyBiscuit.Engine/Save/SaveManager.cs) |

### `SexyBiscuit.Engine.Networking`

| Type | Kind | File |
|---|---|---|
| `ClientRpcAttribute` | class | [`Attributes.cs`](../SexyBiscuit.Engine/Networking/Attributes.cs) |
| `LanDiscovery` | class | [`LanDiscovery.cs`](../SexyBiscuit.Engine/Networking/LanDiscovery.cs) |
| `LanServerInfo` | class | [`LanDiscovery.cs`](../SexyBiscuit.Engine/Networking/LanDiscovery.cs) |
| `NetworkManager` | class | [`NetworkManager.cs`](../SexyBiscuit.Engine/Networking/NetworkManager.cs) |
| `NetworkObject` | class | [`NetworkObject.cs`](../SexyBiscuit.Engine/Networking/NetworkObject.cs) |
| `ReplicateCondition` | enum | [`Attributes.cs`](../SexyBiscuit.Engine/Networking/Attributes.cs) |
| `ReplicatedAttribute` | class | [`Attributes.cs`](../SexyBiscuit.Engine/Networking/Attributes.cs) |
| `ReplicationSystem` | class | [`ReplicationSystem.cs`](../SexyBiscuit.Engine/Networking/ReplicationSystem.cs) |
| `RpcSystem` | class | [`RpcSystem.cs`](../SexyBiscuit.Engine/Networking/RpcSystem.cs) |
| `RpcTarget` | enum | [`Attributes.cs`](../SexyBiscuit.Engine/Networking/Attributes.cs) |
| `ServerRpcAttribute` | class | [`Attributes.cs`](../SexyBiscuit.Engine/Networking/Attributes.cs) |

### `SexyBiscuit.Engine.Localization`

| Type | Kind | File |
|---|---|---|
| `Loc` | class | [`Localization.cs`](../SexyBiscuit.Engine/Localization/Localization.cs) |

### `SexyBiscuit.Engine.Debug`

| Type | Kind | File |
|---|---|---|
| `DebugOverlay` | class | [`DebugOverlay.cs`](../SexyBiscuit.Engine/Debug/DebugOverlay.cs) |
| `GizmoCommand` | struct | [`Gizmos.cs`](../SexyBiscuit.Engine/Debug/Gizmos.cs) |
| `GizmoType` | enum | [`Gizmos.cs`](../SexyBiscuit.Engine/Debug/Gizmos.cs) |
| `Gizmos` | class | [`Gizmos.cs`](../SexyBiscuit.Engine/Debug/Gizmos.cs) |
| `MemorySnapshot` | record | [`MemoryViewer.cs`](../SexyBiscuit.Engine/Debug/MemoryViewer.cs) |
| `MemoryViewer` | class | [`MemoryViewer.cs`](../SexyBiscuit.Engine/Debug/MemoryViewer.cs) |
| `NetworkDiagnostics` | class | [`NetworkDiagnostics.cs`](../SexyBiscuit.Engine/Debug/NetworkDiagnostics.cs) |
| `Profiler` | class | [`Profiler.cs`](../SexyBiscuit.Engine/Debug/Profiler.cs) |

### `SexyBiscuit.Engine.Build`

| Type | Kind | File |
|---|---|---|
| `AssetCooker` | class | [`AssetCooker.cs`](../SexyBiscuit.Engine/Build/AssetCooker.cs) |
| `BuildConfiguration` | enum | [`PlatformConfig.cs`](../SexyBiscuit.Engine/Build/PlatformConfig.cs) |
| `BuildPlatform` | enum | [`PlatformConfig.cs`](../SexyBiscuit.Engine/Build/PlatformConfig.cs) |
| `CookResult` | record | [`AssetCooker.cs`](../SexyBiscuit.Engine/Build/AssetCooker.cs) |
| `ExportPipeline` | class | [`ExportPipeline.cs`](../SexyBiscuit.Engine/Build/ExportPipeline.cs) |
| `ExportResult` | record | [`ExportPipeline.cs`](../SexyBiscuit.Engine/Build/ExportPipeline.cs) |
| `PlatformConfig` | class | [`PlatformConfig.cs`](../SexyBiscuit.Engine/Build/PlatformConfig.cs) |

### `SexyBiscuit.Engine.Steam`

| Type | Kind | File |
|---|---|---|
| `SteamAchievements` | class | [`SteamAchievements.cs`](../SexyBiscuit.Engine/Steam/SteamAchievements.cs) |
| `SteamCloud` | class | [`SteamCloud.cs`](../SexyBiscuit.Engine/Steam/SteamCloud.cs) |
| `SteamLobby` | class | [`SteamLobby.cs`](../SexyBiscuit.Engine/Steam/SteamLobby.cs) |
| `SteamManager` | class | [`SteamManager.cs`](../SexyBiscuit.Engine/Steam/SteamManager.cs) |
| `SteamWorkshop` | class | [`SteamWorkshop.cs`](../SexyBiscuit.Engine/Steam/SteamWorkshop.cs) |

---

## Components you can attach to an Actor

Every non-abstract `Component` subclass, i.e. everything valid in
`actor.AddComponent<T>()`:

| Component | Namespace | Notes |
|---|---|---|
| `Transform` | `Core` | added automatically by the `Actor` constructor |
| `Transform3D` | `Core` | add explicitly for 3D; 3D components auto-add it |
| `SpriteRenderer` | `Rendering` | |
| `TilemapRenderer` | `Rendering` | |
| `ParticleEmitter` | `Rendering` | |
| `Camera2D` | `Rendering` | |
| `Camera3D` | `Rendering` | `Main` requires `Tag == "MainCamera3D"` |
| `MeshRenderer` | `Rendering` | drawn manually, not by the scene graph |
| `Light3D` | `Rendering` | registers into `Light3D.All` |
| `Skybox` | `Rendering` | drawn manually |
| `LODGroup` | `Rendering` | `Update(camera)` driven by you |
| `PostProcessing3D` | `Rendering` | |
| `Rigidbody2D` / `Rigidbody3D` | `Physics` | creates its body in `Awake` |
| `BoxCollider2D`, `CircleCollider2D`, `PolygonCollider2D` | `Physics` | fixture built in `Awake` |
| `BoxCollider3D`, `SphereCollider3D`, `CapsuleCollider3D` | `Physics` | shape registered in `Awake` |
| `CharacterController2D` / `CharacterController3D` | `Physics` | `[RequireComponent]` adds the rigidbody |
| `AudioSource` | `Audio` | listener is an actor tagged `"Camera"` |
| `UiCanvas` | `UI` | the host paints it, not `Component.Draw`; `Space = World` puts it on a quad in the 3D pass |
| `SpriteAnimator` | `Animation` | `[RequireComponent(SpriteRenderer)]` |
| `AnimatorController` | `Animation` | `[RequireComponent(SpriteAnimator)]` |
| `SkeletalAnimator` | `Animation` | produces a bone palette |
| `ScriptComponent` | `Scripting` | set `ScriptPath`, then call `Awake()` |
| `WorldStreamer` | `Scene` | chunk streaming |
| `NetworkObject` | `Networking` | required for replication and RPCs |

`Widget` subclasses are **not** components — they go inside a `Canvas`:
`Label`, `Button`, `Panel`, `Image`, `ProgressBar`, `Slider`, `TextInput`.

---

## Static entry points

| Call | Purpose |
|---|---|
| `SBEngine.Instance` | the running engine |
| `SBEngine.Run(config)` | start with the default engine class |
| `SBEngine.InitializeHosted` / `TickHosted` | run the engine inside an app that owns the device |
| `PhysicsSystem2D.Instance` / `PhysicsSystem3D.Instance` | physics worlds |
| `Tween.Create()` / `Tween.Sequence()` / `UpdateAll` / `KillAll` / `KillAllFor` | tweening |
| `Prefab.Instantiate` / `Save` | prefabs |
| `SceneSerializer.LoadFromFile` / `SaveToFile` | scene files |
| `SceneManager.AdoptScene` | install a scene you built or deserialised |
| `SaveManager.*`, `PlayerPrefs.*` | persistence |
| `AssetBundle.Mount` | bundles |
| `NetworkManager.Instance`, `NetworkObject.Spawn`, `RpcSystem.CallServerRpc` | networking |
| `LanDiscovery.StartBroadcast` / `Discover` | server browser |
| `Gizmos.*`, `DebugOverlay.*`, `Profiler.*`, `MemoryViewer.*`, `NetworkDiagnostics.*` | debug tooling |
| `Theme.Load` / `Apply` | UI skinning |
| `TypeScriptDefinitions.WriteToFile` | script IntelliSense |
| `ExportPipeline.RunCli` | build CLI |
| `SteamManager.Init` / `Shutdown`, `SteamAchievements.*`, `SteamCloud.*`, `SteamLobby.*`, `SteamWorkshop.*` | Steam |
| `SBMath.*`, `Easing.Evaluate` | maths helpers |
| `Time.DeltaTime` etc. | frame timing (see [3](03-game-loop.md#timing)) |

---

## Next

- [21. Gotchas & Known Mismatches](21-gotchas.md)
