#!/usr/bin/env node
// -----------------------------------------------------------------------------
// mirror-check — every engine feature exists twice; this fails a change that
// touched one twin and not the other.
//
// `/mirrors.json` names each C# file's JavaScript twin. A strict pair whose two
// sides do not change together is nearly always a property, a hook, a frame or
// a table that now exists on one engine only, which is the class of bug no test
// on either side can see until a scene crosses over. So it is a check, not a
// convention.
//
//     node tools/mirror-check.js                       # the working tree against HEAD
//     node tools/mirror-check.js --base origin/main    # a range, e.g. in CI
//     node tools/mirror-check.js --allow UiNode        # this run only; or the commit trailer
//                                                      #   Mirror-only: cs UiNode   (or js, or all)
//     node tools/mirror-check.js --of SexyBiscuit.Engine/UI/UiNode.cs   # print a file's twin
//     node tools/mirror-check.js --list
//
// One line per problem, `OK` when there are none.
// -----------------------------------------------------------------------------

import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const repoRoot = path.resolve(here, '..', '..');

/** The map, read once. */
export function loadMirrors(root = repoRoot) {
    return JSON.parse(fs.readFileSync(path.join(root, 'mirrors.json'), 'utf8'));
}

/**
 * Pure: which pairs changed on one side only.
 *
 * @param {Iterable<string>} changed   Repo-relative paths that changed.
 * @param {{name:string, cs:string, js:string, strict?:boolean}[]} pairs
 * @param {{ names?: Set<string>, all?: boolean }} [allow]   Pairs excused this time.
 * @returns {{ failures: string[], warnings: string[] }}
 */
export function findUnmirrored(changed, pairs, allow = {}) {
    const set = new Set([...changed].map((p) => p.replace(/\\/g, '/')));
    const failures = [];
    const warnings = [];

    for (const pair of pairs) {
        const cs = set.has(pair.cs);
        const js = set.has(pair.js);
        if (cs === js) continue;
        if (allow.all || allow.names?.has(pair.name)) continue;

        const [moved, twin] = cs ? [pair.cs, pair.js] : [pair.js, pair.cs];
        const line = `${pair.name}: ${moved} changed, twin ${twin} did not`;
        (pair.strict === false ? warnings : failures).push(line);
    }
    return { failures, warnings };
}

/** Allowances from commit messages in a range: `Mirror-only: cs UiNode`, `Mirror-only: all`. */
export function parseAllowances(messages) {
    const names = new Set();
    let all = false;
    for (const match of messages.matchAll(/^Mirror-only:\s*(cs|js|all)(?:\s+(.+))?$/gim)) {
        if (match[1].toLowerCase() === 'all' && !match[2]) { all = true; continue; }
        for (const name of (match[2] ?? '').split(/[\s,]+/)) if (name) names.add(name);
    }
    return { names, all };
}

function git(args) {
    return execFileSync('git', args, { cwd: repoRoot, encoding: 'utf8' });
}

/** Changed paths: a range against `base`, or the working tree (staged, unstaged and untracked) against HEAD. */
export function changedFiles(base) {
    const lines = (text) => text.split('\n').map((l) => l.trim()).filter(Boolean);
    const diffed = lines(git(['diff', '--name-only', base ?? 'HEAD']));
    const untracked = base ? [] : lines(git(['ls-files', '--others', '--exclude-standard']));
    return new Set([...diffed, ...untracked]);
}

/** The twin(s) of one file, for `--of`. */
export function twinsOf(file, mirrors) {
    const rel = path.relative(repoRoot, path.resolve(repoRoot, file)).replace(/\\/g, '/');
    const out = [];
    for (const pair of mirrors.pairs) {
        if (pair.cs === rel) out.push(`${pair.js}  (${pair.kind}, check: ${pair.check}${pair.strict === false ? ', advisory' : ''})`);
        if (pair.js === rel) out.push(`${pair.cs}  (${pair.kind}, check: ${pair.check}${pair.strict === false ? ', advisory' : ''})`);
    }
    for (const table of mirrors.tables) {
        if (table.path === rel) out.push(`table read by ${table.cs} and ${table.js}`);
        if (table.cs === rel || table.js === rel) out.push(`reads the table ${table.path} (the other reader is ${table.cs === rel ? table.js : table.cs})`);
    }
    return out;
}

// -----------------------------------------------------------------------------
// CLI
// -----------------------------------------------------------------------------

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const args = process.argv.slice(2);
    const mirrors = loadMirrors();
    const flag = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };

    if (args.includes('--list')) {
        for (const pair of mirrors.pairs) console.log(`${pair.name.padEnd(24)} ${pair.kind.padEnd(10)} ${pair.check.padEnd(8)} ${pair.strict === false ? 'advisory' : 'strict  '} ${pair.cs} <-> ${pair.js}`);
        for (const table of mirrors.tables) console.log(`${table.name.padEnd(24)} table               ${table.path}  (${table.cs}, ${table.js})`);
        process.exit(0);
    }
    if (flag('--of') !== undefined) {
        const twins = twinsOf(flag('--of'), mirrors);
        if (twins.length === 0) { console.log('no twin listed in mirrors.json'); process.exit(2); }
        for (const t of twins) console.log(t);
        process.exit(0);
    }

    const base = flag('--base');
    if (base && /^0{7,}$/.test(base)) { console.log('OK (no base commit to compare against)'); process.exit(0); }

    let allow = { names: new Set(), all: false };
    if (base) {
        try { allow = parseAllowances(git(['log', '--format=%B', `${base}..HEAD`])); }
        catch { /* an unknown base: no trailers to read */ }
    }
    for (let i = 0; i < args.length; i++) if (args[i] === '--allow' && args[i + 1]) allow.names.add(args[i + 1]);

    const { failures, warnings } = findUnmirrored(changedFiles(base), mirrors.pairs, allow);
    for (const w of warnings) console.log(`  advisory  ${w}`);
    for (const f of failures) console.error(`  MIRROR    ${f}`);
    if (failures.length > 0) {
        console.error(`${failures.length} pair(s) changed on one side. Change the twin too, or excuse it: --allow <name>, or the commit trailer "Mirror-only: cs|js <name>".`);
        process.exit(1);
    }
    console.log('OK');
}
