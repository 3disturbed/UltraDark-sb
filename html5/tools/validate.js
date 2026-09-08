#!/usr/bin/env node
// -----------------------------------------------------------------------------
// validate — the checker a game gets instead of a screenshot.
//
// Loads every scene of a project through the real deserialiser, confirms every
// script it references exists, compiles every script, and holds each script to
// the shared scripting contract (`src/scripting/bridge-api.json`): a member that
// is not in the contract is what will run in the browser and fail natively, or
// the other way round. One line per problem, `OK` when there are none, so an
// agent can read the result in a glance.
//
//     node html5/tools/validate.js [projectDir ...] [--strict] [--quiet]
//
// With no project the bundled templates are checked. `--strict` fails on
// warnings too, which is the setting for a game about to ship.
// -----------------------------------------------------------------------------

import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { deserialize, ScriptComponent, EngineConfig, SCRIPT_HOOKS } from '../src/index.js';
import { UiCanvas } from '../src/ui/UiCanvas.js';
import { fromJson as uiFromJson } from '../src/ui/UiDocument.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const html5Root = path.join(here, '..');
const repoRoot = path.join(html5Root, '..');

const contract = JSON.parse(fs.readFileSync(path.join(html5Root, 'src', 'scripting', 'bridge-api.json'), 'utf8'));
const GLOBAL_NAMES = Object.keys(contract.globals);
const MEMBER_PATTERN = new RegExp(`\\b(${GLOBAL_NAMES.join('|')})\\.([A-Za-z_$][\\w$]*)`, 'g');
const ARRAY_METHODS = new Set(['length', 'map', 'filter', 'forEach', 'some', 'every', 'find', 'indexOf',
    'slice', 'concat', 'reduce', 'sort', 'includes', 'push', 'join', 'reverse']);

// -----------------------------------------------------------------------------
// The check
// -----------------------------------------------------------------------------

/**
 * Validates one project folder.
 *
 * @param {string} projectDir
 * @returns {{ dir: string, errors: Problem[], warnings: Problem[], scenes: number, scripts: number }}
 * @typedef {{ file: string, line: number, message: string }} Problem
 */
export function validateProject(projectDir) {
    const dir = path.resolve(projectDir);
    const result = { dir, errors: [], warnings: [], scenes: 0, scripts: 0 };

    // The same member twice on one line is one problem.
    const seen = new Set();
    const add = (list, file, line, message) => {
        const key = `${file}:${line}:${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({ file, line, message });
    };
    const error = (file, line, message) => add(result.errors, file, line, message);
    const warn = (file, line, message) => add(result.warnings, file, line, message);

    if (!fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) {
        error('.', 0, `'${projectDir}' is not a directory`);
        return result;
    }

    // ---- ProjectSettings.json ----------------------------------------------------
    const settingsFile = path.join(dir, 'ProjectSettings.json');
    let config = null;
    if (!fs.existsSync(settingsFile)) {
        error('ProjectSettings.json', 0, 'missing');
    } else {
        try {
            config = EngineConfig.fromProjectSettings(fs.readFileSync(settingsFile, 'utf8'));
            if (!config.windowTitle) warn('ProjectSettings.json', 0, 'no WindowTitle; the export will be untitled');
        } catch (err) {
            error('ProjectSettings.json', 0, `does not parse: ${err.message}`);
        }
    }

    // ---- Scenes -------------------------------------------------------------------
    const referencedScripts = new Set();
    const sceneFiles = walk(path.join(dir, 'Scenes'), (f) => f.endsWith('.scene'));
    result.scenes = sceneFiles.length;

    if (config?.startScene) {
        const start = config.startScene.endsWith('.scene') ? config.startScene : `${config.startScene}.scene`;
        if (!fs.existsSync(path.join(dir, start))) {
            error('ProjectSettings.json', 0, `StartScene '${config.startScene}' does not exist`);
        }
    }

    for (const file of sceneFiles) {
        const rel = relative(dir, file);
        let scene;
        try {
            scene = deserialize(fs.readFileSync(file, 'utf8'), {
                onWarning: (message) => warn(rel, 0, message),
            });
        } catch (err) {
            error(rel, 0, `does not load: ${err.message}`);
            continue;
        }

        scene.flushPendingActors();
        for (const actor of scene.allActors) {
            for (const script of actor.getComponents(ScriptComponent)) {
                if (!script.scriptPath) {
                    warn(rel, 0, `'${actor.name}' has a ScriptComponent with no ScriptPath`);
                    continue;
                }
                referencedScripts.add(script.scriptPath.replace(/\\/g, '/'));
                if (!fs.existsSync(path.join(dir, script.scriptPath))) {
                    error(rel, 0, `'${actor.name}' references a script that does not exist: ${script.scriptPath}`);
                }
            }

            // A canvas pointing at a document that is not there paints nothing and says
            // nothing, on either engine, which reads exactly like a layout that went wrong.
            for (const canvas of actor.getComponents(UiCanvas)) {
                if (!canvas.document) continue;
                if (!fs.existsSync(path.join(dir, canvas.document))) {
                    error(rel, 0, `'${actor.name}' references a UI document that does not exist: ${canvas.document}`);
                    continue;
                }
                checkUiDocument(path.join(dir, canvas.document), canvas.document, rel, error);
            }
        }
        scene.destroy();
    }

    // ---- Scripts ------------------------------------------------------------------
    const scriptFiles = walk(path.join(dir, 'Scripts'), (f) => f.endsWith('.js'));
    result.scripts = scriptFiles.length;
    const sources = new Map(scriptFiles.map((f) => [f, fs.readFileSync(f, 'utf8')]));

    // A script can also be attached at runtime: `script.ScriptPath = "Scripts/Tower.js"`.
    for (const source of sources.values()) {
        for (const match of source.matchAll(/["'](Scripts\/[^"']+\.js)["']/g)) referencedScripts.add(match[1]);
    }

    for (const [file, source] of sources) {
        const rel = relative(dir, file);
        checkScript(rel, source, error, warn);
        if (!referencedScripts.has(rel)) warn(rel, 0, 'not referenced by any scene or script');
    }

    return result;
}

/** Holds one script's source to the contract. Exported so a tool can lint a single file. */
/**
 * Parses a `.ui` document so a typo is caught here rather than at run time.
 *
 * The codec refuses an unknown key outright -- a mistyped `childern` would otherwise drop
 * every node below it and leave no trace -- so simply loading the file is the whole check.
 */
function checkUiDocument(fullPath, shownPath, rel, error) {
    try {
        uiFromJson(fs.readFileSync(fullPath, 'utf8'));
    } catch (err) {
        error(rel, 0, `UI document '${shownPath}' is not valid: ${err.message}`);
    }
}

export function checkScript(rel, source, error, warn) {
    if (/^\s*(export|import)\s/m.test(source)) {
        // An ES module exporting a Component subclass: compiled by the browser's loader,
        // and it reaches the engine directly rather than through the contract.
        return;
    }

    try {
        // eslint-disable-next-line no-new
        new vm.Script(source, { filename: rel });
    } catch (err) {
        const at = /:(\d+)\n/.exec(err.stack ?? '');
        error(rel, at ? Number(at[1]) : 0, `does not compile: ${err.message}`);
        return;
    }

    // `var target = Scene.findByTag(...)` followed by `target.transform.x` two lines later
    // is the commonest template bug; a one-line check misses it.
    const arrayVariables = new Set();
    for (const match of source.matchAll(/\b(?:var|let|const)\s+([A-Za-z_$][\w$]*)\s*=\s*Scene\.findByTag\(/g)) {
        arrayVariables.add(match[1]);
    }
    const memberOfArrayVariable = arrayVariables.size > 0
        ? new RegExp(`\\b(${[...arrayVariables].join('|')})\\.(transform|name|tag|active|id|getComponent|destroy)\\b`)
        : null;

    const lines = source.split('\n');
    lines.forEach((text, index) => {
        const line = index + 1;
        const code = stripComments(text);

        for (const match of code.matchAll(MEMBER_PATTERN)) {
            const [, global, member] = match;
            const info = contract.globals[global]?.[member];
            if (info) {
                if (info.shared === false) warn(rel, line, `${global}.${member} exists on one engine only`);
                continue;
            }
            if (global === 'actor') {
                warn(rel, line, `actor.${member} is not in the contract; it lives on this script's proxy only`);
            } else {
                error(rel, line, `${global}.${member} is not in the shared scripting contract`);
            }
        }

        // findByTag returns an array; treating it as one actor is the commonest template bug.
        const arrayUse = /findByTag\([^)]*\)\s*\.\s*([A-Za-z_$][\w$]*)/.exec(code);
        if (arrayUse && !ARRAY_METHODS.has(arrayUse[1])) {
            warn(rel, line, `Scene.findByTag returns an array; use Scene.findFirstByTag for one actor`);
        }
        const laterUse = memberOfArrayVariable?.exec(code);
        if (laterUse) {
            warn(rel, line, `'${laterUse[1]}' holds the array Scene.findByTag returned; use Scene.findFirstByTag for one actor`);
        }

        for (const match of code.matchAll(/\bfunction\s+(on[A-Z]\w*)\s*\(/g)) {
            const name = match[1];
            if (SCRIPT_HOOKS.includes(name)) continue;
            const near = SCRIPT_HOOKS.find((hook) => levenshtein(hook, name) <= 2);
            if (near) warn(rel, line, `'${name}' looks like a misspelling of the '${near}' hook; the engine will never call it`);
        }
    });
}

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

function walk(root, keep) {
    if (!fs.existsSync(root)) return [];
    const found = [];
    for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
        const full = path.join(root, entry.name);
        if (entry.isDirectory()) found.push(...walk(full, keep));
        else if (keep(entry.name)) found.push(full);
    }
    return found.sort();
}

function relative(dir, file) {
    return path.relative(dir, file).replace(/\\/g, '/');
}

function stripComments(line) {
    return line.replace(/\/\/.*$/, '').replace(/"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'/g, '""');
}

function levenshtein(a, b) {
    const rows = Array.from({ length: a.length + 1 }, (_, i) => [i]);
    for (let j = 1; j <= b.length; j++) rows[0][j] = j;
    for (let i = 1; i <= a.length; i++) {
        for (let j = 1; j <= b.length; j++) {
            rows[i][j] = Math.min(
                rows[i - 1][j] + 1,
                rows[i][j - 1] + 1,
                rows[i - 1][j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
        }
    }
    return rows[a.length][b.length];
}

/** Every template folder, the default when no project is named. */
export function templateProjects() {
    const templatesDir = path.join(repoRoot, 'Templates');
    if (!fs.existsSync(templatesDir)) return [];
    return fs.readdirSync(templatesDir)
        .map((name) => path.join(templatesDir, name))
        .filter((dir) => fs.existsSync(path.join(dir, 'ProjectSettings.json')))
        .sort();
}

/** Prints a result the way the CLI does and returns true when it passes. */
export function report(result, { strict = false, quiet = false, log = console.log } = {}) {
    const name = path.basename(result.dir);
    const problems = [
        ...result.errors.map((p) => ({ ...p, kind: 'error' })),
        ...result.warnings.map((p) => ({ ...p, kind: 'warn' })),
    ].sort((a, b) => a.file.localeCompare(b.file) || a.line - b.line);

    if (!quiet || problems.length > 0) {
        for (const p of problems) log(`${name}/${p.file}:${p.line} ${p.kind.padEnd(5)} ${p.message}`);
    }

    const passed = result.errors.length === 0 && (!strict || result.warnings.length === 0);
    if (passed && result.warnings.length === 0) {
        log(`OK: ${name} — ${result.scenes} scene(s), ${result.scripts} script(s)`);
    } else {
        log(`${passed ? 'OK' : 'FAIL'}: ${name} — ${result.errors.length} error(s), ${result.warnings.length} warning(s)`);
    }
    return passed;
}

// -----------------------------------------------------------------------------
// CLI
// -----------------------------------------------------------------------------

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const args = process.argv.slice(2);
    const strict = args.includes('--strict');
    const quiet = args.includes('--quiet');
    const dirs = args.filter((a) => !a.startsWith('--'));
    const projects = dirs.length > 0 ? dirs : templateProjects();

    let failed = 0;
    for (const project of projects) {
        if (!report(validateProject(project), { strict, quiet })) failed++;
    }
    process.exit(failed > 0 ? 1 : 0);
}
