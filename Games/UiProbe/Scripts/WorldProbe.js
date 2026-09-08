// The world-space half of the probe: the same document standing on a wall, floating over
// nothing, and stretched across the screen, all from Assets/UI/terminal.ui.
//
// Nothing here builds a tree. The three canvases in World.scene load that one file, which is
// the claim being tested -- a widget is authored once and the surface decides where it lives.
// This script only orbits the camera and drives a marker, because the things that can be wrong
// (a canvas mirrored, a nameplate that tips over, a panel the colour of mud) are things only an
// eye can see.

var angle = 0;
var lift = 0;

function onStart() {
    // A screen-space marker that chases a point in the world: crisp text that never turns
    // edge-on, which is what a world canvas is deliberately bad at.
    var marker = UI.root.add({
        kind: "label", text: "0 m", scale: 2, tint: "#e8e6e1",
        worldFollow: true, worldAnchor: [-1.2, 1.8, 1.5],
    });

    UI.find("title").text = "SCREEN";
    UI.find("status").text = "same file, stretched over the window";
}

function onUpdate(dt) {
    angle += dt * 0.35;
    lift += dt;

    // Orbit, so the billboard modes can be told apart: the wall panel turns away as you pass
    // it, the nameplate does not, and neither should ever roll.
    var camera = Scene.find("Main Camera");
    if (camera) {
        camera.transform3d.x = Math.sin(angle) * 6;
        camera.transform3d.z = Math.cos(angle) * 6;
        camera.transform3d.rotY = angle * 57.29578;
    }

    var bar = UI.find("power");
    if (bar) bar.value = 0.5 + Math.sin(lift) * 0.45;

    if (UI.find("engage").clicked) UI.find("status").text = "engaged at " + Math.round(lift) + "s";
}
