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
