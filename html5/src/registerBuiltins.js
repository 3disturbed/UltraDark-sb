// -----------------------------------------------------------------------------
// registerBuiltins — imports every module that registers a component or actor.
//
// Registration happens as a side effect of a class's module being evaluated, so
// a scene naming `"SpriteRenderer"` only resolves once that module has loaded.
// Importing the serialiser alone would otherwise leave every component in a
// loaded scene as a MissingComponent — technically lossless, and completely
// useless. The serialiser imports this, so `deserialize` works from any entry
// point.
//
// Nothing here touches `document`, `window` or a GL context at import time, so
// pulling it in from a headless tool or a test is safe.
// -----------------------------------------------------------------------------

import './core/MissingComponent.js';

import './rendering/Camera2D.js';
import './rendering/Camera3D.js';
import './rendering/SpriteRenderer.js';
import './rendering/MeshRenderer.js';
import './rendering/Light3D.js';
import './rendering/Skybox.js';

import './physics/Rigidbody2D.js';
import './physics/Collider2D.js';
import './physics/Rigidbody3D.js';
import './physics/Collider3D.js';
import './physics/CharacterController3D.js';

import './scripting/ScriptComponent.js';

import './gameplay/Pawn.js';
import './gameplay/Controller.js';
import './gameplay/PlayerController.js';
import './gameplay/Character.js';
import './gameplay/GameMode.js';
import './gameplay/GameState.js';
import './gameplay/PlayerState.js';
import './gameplay/PlayerStart.js';

/** True once the built-ins have registered. Importing this module is what does it. */
export const BUILTINS_REGISTERED = true;
