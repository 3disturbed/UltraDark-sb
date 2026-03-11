// CameraFollow.js -- Smooth camera follow for the survival game
// Attach to the CameraRig actor. Lerps toward the Player each frame.

var targetTag   = "Player";
var smoothSpeed = 5.0;
var offsetX     = 0;
var offsetY     = 0;

function onUpdate(dt) {
    var target = Scene.findByTag(targetTag);
    if (!target) return;

    var goalX = target.transform.x + offsetX;
    var goalY = target.transform.y + offsetY;

    // Smooth lerp toward the player position
    var cx = actor.transform.x;
    var cy = actor.transform.y;

    actor.transform.x = cx + (goalX - cx) * smoothSpeed * dt;
    actor.transform.y = cy + (goalY - cy) * smoothSpeed * dt;

    // Also move the main camera so the viewport follows
    var cam = Scene.findByTag("MainCamera");
    if (cam) {
        cam.transform.x = actor.transform.x;
        cam.transform.y = actor.transform.y;
    }
}
