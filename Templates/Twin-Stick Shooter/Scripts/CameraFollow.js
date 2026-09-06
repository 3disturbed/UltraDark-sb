// CameraFollow.js — Smooth camera follow for the twin-stick shooter
// Attached to the Main Camera actor in the Arena scene. Remove the component
// from the camera for a fixed view of a small arena; keep it for a larger one
// where scrolling is needed.

// =============================================================================
// Tuning variables
// =============================================================================

// How quickly the camera catches up to the player. Higher values make the
// camera feel snappier; lower values create a floaty, cinematic feel.
var smoothSpeed = 5.0;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onUpdate(dt) {
    // Find the player actor by name
    var player = Scene.find("Player");
    if (!player) return;

    // Target position is the player's current position
    var targetX = player.transform.x;
    var targetY = player.transform.y;

    // Smoothly interpolate (lerp) the camera toward the target position.
    // The formula: current + (target - current) * speed * dt
    // This creates an exponential ease-out that feels natural.
    var currentX = actor.transform.x;
    var currentY = actor.transform.y;

    actor.transform.x = currentX + (targetX - currentX) * smoothSpeed * dt;
    actor.transform.y = currentY + (targetY - currentY) * smoothSpeed * dt;
}
