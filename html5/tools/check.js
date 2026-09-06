#!/usr/bin/env node
// -----------------------------------------------------------------------------
// check — parses every module and sanity-checks the shader sources.
//
// The shaders live in JavaScript template literals, so a stray backtick or a
// `${` in a GLSL comment silently ends the string and breaks every module that
// imports it. That failure only shows up as a blank page in a browser, which is
// a long way from the line that caused it, so it is worth catching here.
// -----------------------------------------------------------------------------

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(here, '..');

let failures = 0;

function report(kind, file, message) {
    failures++;
    console.error(`  ${kind}  ${path.relative(root, file)}\n         ${message}`);
}

/**
 * Every .js file under the package that is safe to import.
 *
 * `tools/` is skipped because this file lives there, and so does the dev server,
 * which would start listening and never return. `tests/` is skipped because
 * importing a test file runs it, which is what `npm test` is for.
 */
const SKIP_DIRS = new Set(['node_modules', 'tools', 'tests', 'examples']);

function sources(dir) {
    const found = [];
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        if (SKIP_DIRS.has(entry.name) || entry.name.startsWith('.')) continue;
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) found.push(...sources(full));
        else if (entry.name.endsWith('.js')) found.push(full);
    }
    return found;
}

console.log('Parsing modules…');
const files = sources(root);

for (const file of files) {
    try {
        await import(pathToFileURL(file).href);
    } catch (error) {
        // A module that needs a browser is fine; one that will not parse is not.
        if (error instanceof SyntaxError) report('SYNTAX', file, error.message);
    }
}

console.log('Checking shader sources…');
const shaderFile = path.join(root, 'src/rendering/Shaders.js');
const shaderText = fs.readFileSync(shaderFile, 'utf8');

for (const match of shaderText.matchAll(/export const (\w+) = `([\s\S]*?)\n`;/g)) {
    const [, name, source] = match;

    if (source.includes('`')) {
        report('SHADER', shaderFile, `${name} contains a backtick, which ends the template literal.`);
    }
    // `${` interpolates; the only intentional one is the light-count constant,
    // which is substituted deliberately at the top of the fragment shader.
    const interpolations = [...source.matchAll(/\$\{/g)].length;
    if (name === 'STANDARD_FRAGMENT' ? interpolations > 1 : interpolations > 0) {
        report('SHADER', shaderFile, `${name} has an unexpected \${ interpolation.`);
    }
    if (!source.trimStart().startsWith('#version 300 es')) {
        report('SHADER', shaderFile, `${name} does not start with #version 300 es.`);
    }
    // Braces are the usual casualty of an edit that lands in the wrong place.
    const opens = [...source.matchAll(/\{/g)].length;
    const closes = [...source.matchAll(/\}/g)].length;
    if (opens !== closes) {
        report('SHADER', shaderFile, `${name} has ${opens} '{' and ${closes} '}'.`);
    }
}

console.log(`\n${files.length} modules checked.`);
if (failures > 0) {
    console.error(`${failures} problem(s) found.`);
    process.exit(1);
}
console.log('No problems found.');
