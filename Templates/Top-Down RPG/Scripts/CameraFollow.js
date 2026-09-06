// CameraFollow.js — Smooth camera follow for 2D games
// Attach this script to the Camera actor

var targetTag = "Player";
var smoothSpeed = 5.0;
var offsetX = 0;
var offsetY = -50;

function onUpdate(dt) {
    var target = Scene.findFirstByTag(targetTag);
    if (!target) return;

    var targetX = target.transform.x + offsetX;
    var targetY = target.transform.y + offsetY;

    // Smooth lerp towards target
    var currentX = actor.transform.x;
    var currentY = actor.transform.y;

    actor.transform.x = currentX + (targetX - currentX) * smoothSpeed * dt;
    actor.transform.y = currentY + (targetY - currentY) * smoothSpeed * dt;
}
