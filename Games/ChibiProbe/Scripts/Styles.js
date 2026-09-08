// Styles.js — every variant of one slot, side by side.
// Attached to the "Styles" actor in Scenes/Single.scene.
//
// Built with Chibi.setStyle rather than one recipe file per variant, which is
// also the check that setStyle rebuilds a character rather than half of one.

var slot = "hair";            // head, hair, eyes, body, legs or feet
var variants = ["Bald", "Bob", "Spiky", "Ponytail", "Bun", "Long", "Mohawk", "Braids"];
var perRow = 4;
var spacing = 0.62;
var rowDepth = 1.15;

var shown = [];

function onStart() {
    for (var i = 0; i < variants.length; i++) {
        var row = Math.floor(i / perRow);
        var x = (i % perRow - (perRow - 1) / 2) * spacing;

        var chibi = Chibi.spawn("Assets/Characters/Villager.chibi", x, 0, -row * rowDepth);
        if (!chibi) continue;

        // A quarter turn on the back row, so a style is seen from more than
        // straight on -- hair that looks fine head-on can still be a slab.
        if (row > 0) chibi.transform3d.rotY = 20;

        shown.push({ actor: chibi, variant: variants[i] });
        Chibi.play(chibi, "idle");
    }
}

var applied = false;

function onUpdate() {
    // The recipe is read from disk, so the body does not exist on the first
    // frame and there is nothing yet to restyle.
    if (applied || shown.length === 0) return;

    var any = false;
    for (var i = 0; i < shown.length; i++) {
        if (Chibi.setStyle(shown[i].actor, slot, shown[i].variant)) any = true;
    }
    if (!any) return;

    applied = true;
    log("Styles: " + slot + " — " + variants.join(", "));
}
