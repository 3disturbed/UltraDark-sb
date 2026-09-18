// The template smoke, on this project: every scene loads, every script compiles and runs 120
// frames with no host, and says nothing on stderr.
import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { projectRoot, engineImport } from "./helpers/engine.mjs";

const { deserialize, ScriptComponent } = await engineImport("src/index.js");
const { Prefab, prefabPathsIn } = await engineImport("src/scene/Prefab.js");

test("Scenes/Main.scene runs its scripts for 120 frames without a script error", () => {
  const errors = [];
  const realError = console.error;
  console.error = (...args) => errors.push(args.join(" "));
  try {
    const text = fs.readFileSync(path.join(projectRoot, "Scenes/Main.scene"), "utf8");
    Prefab.clear();
    for (const prefabPath of prefabPathsIn(text)) {
      const file = path.join(projectRoot, prefabPath);
      if (fs.existsSync(file)) Prefab.register(prefabPath, JSON.parse(fs.readFileSync(file, "utf8")));
      else errors.push(`missing prefab ${prefabPath}`);
    }
    const scene = deserialize(text, { onWarning: () => {} });
    scene.flushPendingActors();
    const loaded = new WeakSet();
    const loadScripts = () => {
      for (const actor of scene.allActors) {
        for (const script of actor.getComponents(ScriptComponent)) {
          if (loaded.has(script) || !script.scriptPath) continue;
          loaded.add(script);
          const file = path.join(projectRoot, script.scriptPath);
          if (!fs.existsSync(file)) { errors.push(`missing script ${script.scriptPath}`); continue; }
          script.setSource(fs.readFileSync(file, "utf8"), {
            read: (modulePath) => {
              const moduleFile = path.join(projectRoot, modulePath);
              return fs.existsSync(moduleFile) ? fs.readFileSync(moduleFile, "utf8") : null;
            },
          });
        }
      }
    };
    for (let frame = 0; frame < 120; frame++) {
      loadScripts();
      scene.update(1 / 60);
      scene.lateUpdate(1 / 60);
      scene.flushPendingActors();
    }
  } finally {
    console.error = realError;
  }
  assert.deepEqual(errors.filter((e) => !/UiTextMeasure/.test(e)), []);
});

// The scene alone, deserialised and run for nothing: no script, no frame. What a player sees until
// the entry script has been fetched and started is whatever the scene file holds, and with no camera
// in it the 3D pass draws nothing, so the engine's clear colour shows -- cornflower blue, which
// neither engine takes from ProjectSettings.json. 2.0.0 opened on that blue. The scene therefore
// carries the stage's camera and its night sky itself, and the stage adopts them (camera.js, arena.js).
test("before any script runs, the scene already has the stage's camera and a dark sky", async () => {
  const { Camera3D } = await engineImport("src/rendering/Camera3D.js");
  const { Skybox } = await engineImport("src/rendering/Skybox.js");
  const text = fs.readFileSync(path.join(projectRoot, "Scenes/Main.scene"), "utf8");
  const scene = deserialize(text, { onWarning: () => {} });
  scene.flushPendingActors();
  const actors = [...scene.allActors];

  const cameras = actors.filter((a) => a.getComponent(Camera3D));
  assert.equal(cameras.length, 1, "one camera, authored");
  assert.equal(cameras[0].name, "StageCamera", "under the name the rig adopts");
  assert.equal(cameras[0].tag, "MainCamera3D", "with the one tag the native Camera3D.Main accepts");

  // And they come BEFORE the actor that carries the script. The native engine reads a script from
  // disk and starts it while the scene is still loading, so an actor later in the file does not
  // exist yet when onStart looks for it: the rig found no camera, made a second, and the native
  // Camera3D.Main went on rendering from the authored one, which nothing moves. (A browser fetches
  // the script, so there the scene is whole by then and the order never showed.)
  const order = JSON.parse(text).layers.flatMap((layer) => layer.actors);
  const firstScript = order.findIndex((a) => (a.components ?? []).some((c) => c.type === "ScriptComponent"));
  for (const name of ["StageCamera", "Skybox"]) {
    const at = order.findIndex((a) => a.name === name);
    assert.ok(at >= 0 && at < firstScript, `${name} is authored ahead of the script that adopts it (${at} vs ${firstScript})`);
  }

  const skies = actors.map((a) => a.getComponent(Skybox)).filter(Boolean);
  assert.equal(skies.length, 1, "one sky, authored");
  for (const colour of [skies[0].gradientTop, skies[0].gradientBottom]) {
    assert.ok(Math.max(colour.r, colour.g, colour.b) <= 48, `the sky is a night sky (${colour.r}, ${colour.g}, ${colour.b})`);
  }
});
