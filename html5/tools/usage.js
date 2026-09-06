#!/usr/bin/env node
// -----------------------------------------------------------------------------
// usage — what a Claude Code session spent, and on which tools.
//
// Reads a session transcript (`~/.claude/projects/<slug>/<session>.jsonl`) and
// reports the numbers that decide the cost of a game: API calls, the context
// each call carried, how much of it was served from cache, output tokens, and
// the tools whose results added the most text. This is the measurement behind
// the estimates in wiki/27-game-factory-workflow.md.
//
//     node html5/tools/usage.js <transcript.jsonl> [--top 10] [--json]
//     node html5/tools/usage.js --latest [projectDir] [--top 10] [--json]
//
// `--latest` picks the newest transcript for the project (the current directory
// by default), using the folder-name rule Claude Code uses.
// -----------------------------------------------------------------------------

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

/** Tokens an image costs, near enough: (w × h) / 750 at the editor's default capture. */
const IMAGE_TOKENS = 800;

/** Dollars per million tokens: [uncached input, cache write, cache read, output]. */
const PRICES = {
    'claude-fable-5-1': [10, 12.5, 0.25, 50],
    'claude-fable-5': [10, 12.5, 1, 50],
    'claude-opus-5': [5, 6.25, 0.5, 25],
    'claude-opus-4-8': [5, 6.25, 0.5, 25],
    'claude-opus-4-7': [5, 6.25, 0.5, 25],
    'claude-sonnet-5': [2, 2.5, 0.2, 10],
    'claude-sonnet-4-6': [3, 3.75, 0.3, 15],
    'claude-haiku-4-5': [1, 1.25, 0.1, 5],
};

/**
 * Sums a transcript.
 *
 * @param {string} file
 * @returns {Summary}
 * @typedef {object} Summary
 * @property {number} calls API calls (assistant messages, deduplicated by request id)
 * @property {number} prompts messages the user typed
 * @property {number} input uncached input tokens
 * @property {number} cacheWrite
 * @property {number} cacheRead
 * @property {number} output
 * @property {number} thinking output tokens that were thinking
 * @property {number} contextPerCall mean tokens presented per call (input + cache write + cache read)
 * @property {number} cacheShare fraction of presented context served from cache
 * @property {number} costUsd estimate from the price table, 0 when the model is unknown
 * @property {string[]} models
 * @property {{ name: string, calls: number, inputChars: number, resultChars: number, resultTokens: number }[]} tools
 */
export function summarise(file) {
    const calls = new Map();          // requestId -> usage
    const toolUses = new Map();       // tool_use id -> { name, inputChars }
    const tools = new Map();          // name -> totals
    const models = new Set();
    let prompts = 0;

    for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
        if (!line.trim()) continue;
        let entry;
        try { entry = JSON.parse(line); } catch { continue; }
        if (entry.isSidechain) continue;   // a subagent's transcript is billed to the subagent

        const message = entry.message;
        if (!message || typeof message !== 'object') continue;

        if (entry.type === 'assistant') {
            const key = entry.requestId ?? entry.uuid;
            if (message.usage && !calls.has(key)) {
                calls.set(key, message.usage);
                if (message.model) models.add(message.model);
            }
            for (const block of blocks(message.content)) {
                if (block.type !== 'tool_use') continue;
                const inputChars = JSON.stringify(block.input ?? {}).length;
                toolUses.set(block.id, { name: block.name, inputChars });
                const tool = tools.get(block.name) ?? { name: block.name, calls: 0, inputChars: 0, resultChars: 0, resultTokens: 0 };
                tool.calls++;
                tool.inputChars += inputChars;
                tools.set(block.name, tool);
            }
        } else if (entry.type === 'user') {
            let sawResult = false;
            for (const block of blocks(message.content)) {
                if (block.type !== 'tool_result') continue;
                sawResult = true;
                const use = toolUses.get(block.tool_use_id);
                if (!use) continue;
                const { chars, tokens } = measure(block.content);
                const tool = tools.get(use.name);
                if (tool) {
                    tool.resultChars += chars;
                    tool.resultTokens += tokens;
                }
            }
            if (!sawResult && (typeof message.content === 'string' || blocks(message.content).some((b) => b.type === 'text'))) {
                prompts++;
            }
        }
    }

    let input = 0, cacheWrite = 0, cacheRead = 0, output = 0, thinking = 0, costUsd = 0;
    for (const [, usage] of calls) {
        input += usage.input_tokens ?? 0;
        cacheWrite += usage.cache_creation_input_tokens ?? 0;
        cacheRead += usage.cache_read_input_tokens ?? 0;
        output += usage.output_tokens ?? 0;
        thinking += usage.output_tokens_details?.thinking_tokens ?? 0;
    }

    const model = [...models][0];
    const price = PRICES[model];
    if (price) {
        costUsd = (input * price[0] + cacheWrite * price[1] + cacheRead * price[2] + output * price[3]) / 1e6;
    }

    const presented = input + cacheWrite + cacheRead;
    return {
        file,
        calls: calls.size,
        prompts,
        input, cacheWrite, cacheRead, output, thinking,
        contextPerCall: calls.size > 0 ? Math.round(presented / calls.size) : 0,
        cacheShare: presented > 0 ? cacheRead / presented : 0,
        costUsd,
        models: [...models],
        tools: [...tools.values()].sort((a, b) => b.resultTokens - a.resultTokens),
    };
}

function blocks(content) {
    return Array.isArray(content) ? content : [];
}

/** Characters and estimated tokens of a tool result; images count a flat amount. */
function measure(content) {
    if (typeof content === 'string') return { chars: content.length, tokens: Math.ceil(content.length / 4) };
    let chars = 0, tokens = 0;
    for (const block of blocks(content)) {
        if (block.type === 'text') {
            chars += block.text?.length ?? 0;
            tokens += Math.ceil((block.text?.length ?? 0) / 4);
        } else if (block.type === 'image') {
            tokens += IMAGE_TOKENS;
        }
    }
    return { chars, tokens };
}

/** The transcript folder Claude Code keeps for a project directory. */
export function transcriptDir(projectDir) {
    const slug = path.resolve(projectDir).replace(/[^A-Za-z0-9]/g, '-');
    return path.join(os.homedir(), '.claude', 'projects', slug);
}

/** The newest transcript for a project, or null. */
export function latestTranscript(projectDir) {
    const dir = transcriptDir(projectDir);
    if (!fs.existsSync(dir)) return null;
    const files = fs.readdirSync(dir)
        .filter((f) => f.endsWith('.jsonl'))
        .map((f) => path.join(dir, f))
        .sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
    return files[0] ?? null;
}

/** The report as lines. */
export function format(summary, { top = 10 } = {}) {
    const k = (n) => n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
    const lines = [
        `${path.basename(summary.file)}  (${summary.models.join(', ') || 'model unknown'})`,
        `  prompts ${summary.prompts} · API calls ${summary.calls} · context/call ${k(summary.contextPerCall)} tokens (${Math.round(summary.cacheShare * 100)}% from cache)`,
        `  input ${k(summary.input)} · cache write ${k(summary.cacheWrite)} · cache read ${k(summary.cacheRead)} · output ${k(summary.output)} (thinking ${k(summary.thinking)})`,
        summary.costUsd > 0 ? `  estimated cost $${summary.costUsd.toFixed(2)}` : '  estimated cost: model not in the price table',
    ];
    if (summary.tools.length > 0) {
        lines.push('  tools by result size:');
        for (const tool of summary.tools.slice(0, top)) {
            lines.push(`    ${tool.name.padEnd(28)} ${String(tool.calls).padStart(4)} calls  ${k(tool.resultTokens).padStart(7)} tokens back  ${k(Math.ceil(tool.inputChars / 4)).padStart(7)} tokens in`);
        }
    }
    return lines;
}

// -----------------------------------------------------------------------------
// CLI
// -----------------------------------------------------------------------------

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const args = process.argv.slice(2);
    const take = (flag) => { const i = args.indexOf(flag); return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined; };
    const top = Number(take('--top') ?? 10);
    const positional = args.filter((a, i) => !a.startsWith('--') && args[i - 1] !== '--top');

    let file;
    if (args.includes('--latest')) {
        file = latestTranscript(positional[0] ?? process.cwd());
        if (!file) {
            console.error(`no transcripts under ${transcriptDir(positional[0] ?? process.cwd())}`);
            process.exit(2);
        }
    } else {
        file = positional[0];
        if (!file || !fs.existsSync(file)) {
            console.error('usage: node html5/tools/usage.js <transcript.jsonl> | --latest [projectDir]  [--top N] [--json]');
            process.exit(2);
        }
    }

    const summary = summarise(file);
    if (args.includes('--json')) console.log(JSON.stringify(summary, null, 2));
    else for (const line of format(summary, { top })) console.log(line);
}
