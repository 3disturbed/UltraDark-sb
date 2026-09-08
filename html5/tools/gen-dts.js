#!/usr/bin/env node
// -----------------------------------------------------------------------------
// gen-dts — writes the TypeScript declarations for the scripting API from the
// contract, so the API a script sees in an editor, the one the MCP resource hands
// an agent and the one both bridges implement are one list.
//
// `src/scripting/bridge-api.json` is the source. This writes
// `src/scripting/sb-engine.d.ts`; the C# assembly embeds that file and serves
// it through TypeScriptDefinitions. Never edit the .d.ts by hand.
//
//     node tools/gen-dts.js            # regenerate
//     node tools/gen-dts.js --check    # exit 1 when the checked-in file is stale
// -----------------------------------------------------------------------------

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const contractPath = path.join(here, '..', 'src', 'scripting', 'bridge-api.json');
export const outputPath = path.join(here, '..', 'src', 'scripting', 'sb-engine.d.ts');

export const HEADER = '// SexyBiscuit Engine — Script API Declarations';

/** TypeScript builtins a type expression may name without declaring them. */
export const BUILTIN_TYPES = new Set(['Record', 'Array', 'Partial', 'Readonly', 'Promise', 'Map', 'Set']);

function docLines(text, indent) {
    if (!text) return [];
    const wrapped = wrap(text, 92 - indent.length);
    if (wrapped.length === 1) return [`${indent}/** ${wrapped[0]} */`];
    return [`${indent}/**`, ...wrapped.map((l) => `${indent} * ${l}`), `${indent} */`];
}

function wrap(text, width) {
    const words = text.split(/\s+/);
    const lines = [];
    let line = '';
    for (const word of words) {
        if (line && (line + ' ' + word).length > width) { lines.push(line); line = word; }
        else line = line ? `${line} ${word}` : word;
    }
    if (line) lines.push(line);
    return lines;
}

function signature(name, member) {
    if (member.params !== undefined) {
        const params = member.params.map((p) => `${p.variadic ? '...' : ''}${p.name}${p.optional ? '?' : ''}: ${p.type}`).join(', ');
        return `${name}(${params}): ${member.returns ?? 'void'};`;
    }
    return `${member.readonly ? 'readonly ' : ''}${name}: ${member.type ?? 'any'};`;
}

function memberLines(members, indent, extra = {}) {
    const lines = [];
    for (const [name, member] of Object.entries(members)) {
        lines.push(...docLines(member.doc, indent));
        lines.push(`${indent}${signature(name, member)}`);
    }
    if (extra.index) lines.push(`${indent}[property: string]: ${extra.index};`);
    return lines;
}

function interfaceBlock(name, members, { doc, extends: ext, index } = {}) {
    return [
        ...docLines(doc, ''),
        `declare interface ${name}${ext ? ` extends ${ext}` : ''} {`,
        ...memberLines(members, '    ', { index }),
        '}',
        '',
    ];
}

/** The whole .d.ts for a contract object. */
export function generateDts(contract) {
    const out = [
        '// =============================================================================',
        HEADER,
        '// Generated from html5/src/scripting/bridge-api.json by html5/tools/gen-dts.js.',
        '// Do not edit: change the contract and run `npm run gen` in html5/.',
        '//',
        '// This surface is shared with the HTML5 runtime: a script that uses only what is',
        '// declared here runs unchanged under both engines. Place this file beside your',
        '// Scripts/ and reference it from jsconfig.json for completion in an editor.',
        '// =============================================================================',
        '',
    ];

    // Helper interfaces the members name.
    for (const [name, type] of Object.entries(contract.types ?? {})) {
        out.push(...interfaceBlock(name, type.members ?? {}, { doc: type.doc, extends: type.extends, index: type.index }));
    }

    // The sections that are interfaces: a found actor, its transform, a collision, a UI node.
    const sections = contract.meta?.sections ?? {};
    for (const [section, meta] of Object.entries(sections)) {
        out.push(...interfaceBlock(meta.interface, contract[section], { doc: meta.doc }));
    }

    // Globals. A global with an interface (transform3d) is declared as that interface, nullable.
    for (const [name, members] of Object.entries(contract.globals)) {
        const meta = contract.meta?.globals?.[name] ?? {};
        out.push('// ' + '-'.repeat(77));
        out.push(`// ${name} — ${meta.doc ?? ''}`.trimEnd());
        out.push('// ' + '-'.repeat(77));
        if (meta.interface) {
            out.push(...interfaceBlock(meta.interface, members, { doc: meta.doc }));
            out.push(`declare const ${name}: ${meta.interface}${meta.nullable ? ' | null' : ''};`);
        } else {
            out.push(`declare const ${name}: {`, ...memberLines(members, '    '), '};');
        }
        out.push('');
    }

    out.push('// ' + '-'.repeat(77));
    out.push('// Bare functions');
    out.push('// ' + '-'.repeat(77));
    for (const [name, member] of Object.entries(contract.bare)) {
        out.push(...docLines(member.doc, ''));
        out.push(`declare function ${signature(name, member)}`);
    }
    out.push('');

    out.push('// ' + '-'.repeat(77));
    out.push('// Lifecycle hooks — define the ones a script needs');
    out.push('// ' + '-'.repeat(77));
    for (const [name, hook] of Object.entries(contract.hooks)) {
        out.push(...docLines(hook.doc, ''));
        out.push(`declare function ${signature(name, { params: hook.params ?? [], returns: hook.returns ?? 'void' })}`);
    }
    out.push('');
    return out.join('\n');
}

/** Every capitalised identifier a type expression names, quoted literals removed. */
export function typeNames(expression) {
    return [...expression.replace(/"[^"]*"|'[^']*'/g, '').matchAll(/\b([A-Z]\w*)\b/g)].map((m) => m[1]);
}

/** The interface names the contract declares. */
export function declaredTypes(contract) {
    const names = new Set(Object.keys(contract.types ?? {}));
    for (const meta of Object.values(contract.meta?.sections ?? {})) names.add(meta.interface);
    for (const meta of Object.values(contract.meta?.globals ?? {})) if (meta.interface) names.add(meta.interface);
    return names;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const contract = JSON.parse(fs.readFileSync(contractPath, 'utf8'));
    const text = generateDts(contract);
    if (process.argv.includes('--check')) {
        const current = fs.existsSync(outputPath) ? fs.readFileSync(outputPath, 'utf8') : '';
        if (current !== text) {
            const a = current.split('\n'), b = text.split('\n');
            const i = a.findIndex((line, n) => line !== b[n]);
            console.error(`${path.relative(process.cwd(), outputPath)} is stale (first difference at line ${i + 1}). Run: npm run gen`);
            process.exit(1);
        }
        console.log('sb-engine.d.ts is current.');
    } else {
        fs.writeFileSync(outputPath, text);
        console.log(`wrote ${path.relative(process.cwd(), outputPath)} (${text.split('\n').length} lines)`);
    }
}
