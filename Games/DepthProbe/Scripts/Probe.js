// RED is created FIRST but has the HIGH depth; BLUE is created SECOND with the LOW depth.
// Sorting by depth  -> RED wins (high = front).
// Submission order  -> BLUE wins (drawn later).
function onStart() {
    var red = Scene.createActor("Red", 0, 0);
    Scene.addComponent(red, "SpriteRenderer",
        { Tint: { R: 255, G: 0, B: 0, A: 255 }, Size: [4000, 4000], LayerDepth: 0.90 });

    var blue = Scene.createActor("Blue", 0, 0);
    Scene.addComponent(blue, "SpriteRenderer",
        { Tint: { R: 0, G: 0, B: 255, A: 255 }, Size: [4000, 4000], LayerDepth: 0.10 });

    var sr = red.getComponent("SpriteRenderer");
    log("red LayerDepth reads back as: " + (sr ? sr.layerDepth : "no component"));
    log("red Size reads back as: " + (sr && sr.size ? sr.size.x + "x" + sr.size.y : "unset"));
}
