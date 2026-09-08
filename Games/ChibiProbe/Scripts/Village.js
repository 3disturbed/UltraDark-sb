// Village.js — everything the Chibi namespace can do, in one screen.
// Attached to the "Village" actor in Scenes/Main.scene.

var rows = 2;                 // rows of villagers
var perRow = 7;               // and how many stand in each
var spacing = 0.85;
var firstSeed = 1000;

var clips = ["idle", "walk", "run", "wave", "cheer", "jump", "hit", "sit", "die"];
var cycleSeconds = 3.0;

var villagers = [];
var clipIndex = 0;
var elapsed = 0;

function onStart() {
    for (var row = 0; row < rows; row++) {
        for (var i = 0; i < perRow; i++) {
            var seed = firstSeed + row * perRow + i;
            var x = (i - (perRow - 1) / 2) * spacing;
            var z = -row * 1.5;

            var villager = Chibi.random(seed, x, 0, z);
            if (!villager) continue;

            villagers.push(villager);
            Chibi.play(villager, "idle");
        }
    }

    // One of them carries a torch, to show a socket holding something the engine
    // did not build: the actor is ordinary, the hand is a place to put it.
    if (villagers.length > 0) {
        var torch = Scene.createActor("Torch", 0, 0);
        Scene.addComponent(torch, "MeshRenderer", {
            meshType: "Capsule",
            materials: [{ albedoColor: "#C96F4AFF", emissiveIntensity: 0.6 }],
        });
        Chibi.attach(villagers[0], "Hand_R", torch);
        torch.transform3d.y = -0.12;
        torch.transform3d.scaleX = 0.03;
        torch.transform3d.scaleY = 0.10;
        torch.transform3d.scaleZ = 0.03;
    }

    log("ChibiProbe: " + villagers.length + " villagers");
}

function onUpdate(dt) {
    elapsed += dt;
    if (elapsed < cycleSeconds) return;

    elapsed = 0;
    clipIndex = (clipIndex + 1) % clips.length;

    for (var i = 0; i < villagers.length; i++) {
        Chibi.play(villagers[i], clips[clipIndex], 0.2);
    }
    log("clip: " + clips[clipIndex]);
}
