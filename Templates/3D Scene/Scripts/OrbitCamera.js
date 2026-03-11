// OrbitCamera.js — Mouse-driven orbit camera for 3D scenes
// Attach this script to the Camera actor

var targetX = 0;
var targetY = 1;
var targetZ = 0;
var distance = 10;
var rotationX = 25;  // pitch (degrees)
var rotationY = 45;  // yaw (degrees)
var zoomSpeed = 2;
var rotateSpeed = 0.3;
var minDistance = 2;
var maxDistance = 50;
var minPitch = -80;
var maxPitch = 80;

var isDragging = false;
var lastMouseX = 0;
var lastMouseY = 0;

function onStart() {
    log("Orbit Camera ready!");
    log("Right-click drag to rotate, scroll to zoom.");
    updateCameraPosition();
}

function onUpdate(dt) {
    var mouseX = Input.mouseX;
    var mouseY = Input.mouseY;

    // Right mouse button to rotate
    if (Input.isMouseHeld(1)) {
        if (!isDragging) {
            isDragging = true;
            lastMouseX = mouseX;
            lastMouseY = mouseY;
        }

        var deltaX = mouseX - lastMouseX;
        var deltaY = mouseY - lastMouseY;

        rotationY += deltaX * rotateSpeed;
        rotationX -= deltaY * rotateSpeed;

        // Clamp pitch
        if (rotationX < minPitch) rotationX = minPitch;
        if (rotationX > maxPitch) rotationX = maxPitch;

        lastMouseX = mouseX;
        lastMouseY = mouseY;
    } else {
        isDragging = false;
    }

    // Scroll to zoom
    var scroll = Input.scrollDelta;
    if (scroll !== 0) {
        distance -= scroll * zoomSpeed;
        if (distance < minDistance) distance = minDistance;
        if (distance > maxDistance) distance = maxDistance;
    }

    updateCameraPosition();
}

function updateCameraPosition() {
    // Convert spherical coordinates to cartesian
    var pitchRad = rotationX * Math.PI / 180;
    var yawRad = rotationY * Math.PI / 180;

    var cosP = Math.cos(pitchRad);
    var sinP = Math.sin(pitchRad);
    var cosY = Math.cos(yawRad);
    var sinY = Math.sin(yawRad);

    actor.transform3d.x = targetX + distance * cosP * sinY;
    actor.transform3d.y = targetY + distance * sinP;
    actor.transform3d.z = targetZ + distance * cosP * cosY;

    // Look at target (simplified — engine handles this via Camera3D.LookAt)
}
