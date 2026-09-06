// -----------------------------------------------------------------------------
// Every bundled template's scripts run in the browser engine.
//
// A template scene that loads but whose scripts throw on the first frame is the
// failure a playtester meets. This runs each template's scripts for sixty frames
// with no host (no input, no audio, no physics), which is exactly how a validator
// or a headless tool would, and fails on the first script error or compile
// failure. The C# suite has the same test for Jint, so a template that passes
// both runs under both engines.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { deserialize, ScriptComponent } from '../src/index.js';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const templatesDir = path.join(repoRoot, 'Templates');

function templates() {
    if (!fs.existsSync(templatesDir)) return [];
    return fs.readdirSync(templatesDir)
        .filter((name) => fs.existsSync(path.join(templatesDir, name, 'Scenes')))
        .sort();
}

/** Runs one scene's scripts for sixty frames, returning every error line they produced. */
export function runTemplateScene(templateDir, sceneFile, frames = 60) {
    const errors = [];
    const originalError = console.error;
    console.error = (...args) => errors.push(args.join(' '));

    try {
        const scene = deserialize(fs.readFileSync(sceneFile, 'utf8'), { onWarning: () => {} });
        scene.flushPendingActors();

        let scripts = 0;
        for (const actor of scene.allActors) {
            for (const script of actor.getComponents(ScriptComponent)) {
                const file = path.join(templateDir, script.scriptPath);
                if (!fs.existsSync(file)) {
                    errors.push(`missing script ${script.scriptPath}`);
                    continue;
                }
                script.setSource(fs.readFileSync(file, 'utf8'));
                scripts++;
            }
        }

        for (let i = 0; i < frames; i++) {
            scene.update(1 / 60);
            scene.fixedUpdate(1 / 60);
            scene.lateUpdate(1 / 60);
        }
        scene.destroy();

        return { scripts, errors };
    } finally {
        console.error = originalError;
    }
}

for (const template of templates()) {
    test(`the ${template} template's scripts run without errors`, () => {
        const templateDir = path.join(templatesDir, template);
        const scenesDir = path.join(templateDir, 'Scenes');
        let scriptsSeen = 0;

        for (const file of fs.readdirSync(scenesDir).filter((f) => f.endsWith('.scene'))) {
            const { scripts, errors } = runTemplateScene(templateDir, path.join(scenesDir, file));
            scriptsSeen += scripts;
            assert.deepEqual([...new Set(errors)], [], `${template}/${file} produced script errors`);
        }

        if (template !== 'Empty') {
            assert.ok(scriptsSeen > 0, `${template} has no script components to run`);
        }
    });
}
