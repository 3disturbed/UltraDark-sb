// CameraFollow.js — Smooth camera follow for the twin-stick shooter
// Attach this script to the Main Camera actor if you want the camera to
// track the player. By default the Arena scene uses a fixed camera at the
// center of the 1280x720 arena, but this script can be added for larger
// arenas where scrolling is needed.

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
