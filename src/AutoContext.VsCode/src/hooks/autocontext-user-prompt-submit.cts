#!/usr/bin/env node
// AutoContext UserPromptSubmit hook (shipped via the bundled agent-plugin).
//
// Runs once per user turn, before the prompt reaches the model. Emits an
// `additionalContext` block listing the AutoContext discovery surface
// (LM tools + MCP tools) and — when the prompt matches a known
// route — a focused recommendation naming the most relevant tool(s)
// and instruction file(s). The model receives this as system context;
// the user's prompt itself is unmodified.
//
// Layout (inside the installed extension):
//   <extension>/resources/mcp-tools.json
//   <extension>/resources/instructions-files.metadata.json
//   <extension>/plugin/.claude-plugin/plugin.json
//   <extension>/plugin/hooks/hooks.json
//   <extension>/plugin/scripts/autocontext-user-prompt-submit.cjs   ← compiled output
//
// Authored as `.cts` so TypeScript emits CommonJS regardless of the
// extension's `"type": "module"` package.json. `tsc` writes the
// resulting `.cjs` into `dist/hooks/`; the build then copies it into
// `plugin/scripts/` so it sits alongside the plugin manifest.
//
// State sources (pre-daemon): on-disk JSON manifests read directly.
// When the autoctx daemon ships, this hook re-points at AutoctxClient
// (Instructions.List, Tools.List); routing logic and output shape stay.

import * as fs from 'node:fs';
import * as path from 'node:path';

// __dirname = <extension>/plugin/scripts
// EXTENSION_ROOT = <extension>
const EXTENSION_ROOT = path.resolve(__dirname, '..', '..');
const RESOURCES_DIR = path.join(EXTENSION_ROOT, 'resources');
const PACKAGE_JSON_PATH = path.join(EXTENSION_ROOT, 'package.json');

interface UserPromptSubmitInput {
    readonly session_id?: string;
    readonly transcript_path?: string;
    readonly cwd?: string;
    readonly hook_event_name?: string;
    readonly prompt?: string;
}

interface UserPromptSubmitOutput {
    readonly hookSpecificOutput: {
        readonly hookEventName: 'UserPromptSubmit';
        readonly additionalContext: string;
    };
}

interface JsonMcpTool {
    readonly name: string;
    readonly description?: string;
    readonly categories?: readonly string[];
}

interface JsonMcpToolsManifest {
    readonly tools?: readonly JsonMcpTool[];
}

interface JsonInstructionsFileMetadata {
    readonly fileName: string;
    readonly description?: string;
    readonly applyTo?: string;
}

interface JsonInstructionsFilesMetadata {
    readonly instructions?: readonly JsonInstructionsFileMetadata[];
}

interface JsonLanguageModelTool {
    readonly name: string;
}

interface JsonPackageManifest {
    readonly contributes?: {
        readonly languageModelTools?: readonly JsonLanguageModelTool[];
    };
}

interface RouteIndex {
    /** Lowercased category name -> tool names tagged with that category. */
    readonly categoryToTools: ReadonlyMap<string, readonly string[]>;
    /** Lowercased file extension (with leading dot) -> instruction file names. */
    readonly extToInstructions: ReadonlyMap<string, readonly string[]>;
    /** All MCP tool names (for the static block). */
    readonly mcpToolNames: readonly string[];
    /** All Language-Model tool names contributed by this extension's package.json. */
    readonly lmToolNames: readonly string[];
}

function drainStdin(): Promise<string> {
    return new Promise((resolve) => {
        let buf = '';
        process.stdin.on('data', (chunk) => {
            buf += String(chunk);
        });
        process.stdin.on('end', () => resolve(buf));
        process.stdin.on('error', () => resolve(buf));
    });
}

function parseInput(raw: string): UserPromptSubmitInput {
    const trimmed = raw.trim();
    if (trimmed.length === 0) {
        return {};
    }
    try {
        return JSON.parse(trimmed) as UserPromptSubmitInput;
    } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        process.stderr.write(
            `[autocontext-user-prompt-submit] could not parse stdin JSON: ${message}\n`,
        );
        return {};
    }
}

function readJsonFromPath<T>(fullPath: string): T | null {
    try {
        const raw = fs.readFileSync(fullPath, 'utf8');
        return JSON.parse(raw) as T;
    } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        process.stderr.write(
            `[autocontext-user-prompt-submit] could not read ${fullPath}: ${message}\n`,
        );
        return null;
    }
}

function readResourceJson<T>(fileName: string): T | null {
    return readJsonFromPath<T>(path.join(RESOURCES_DIR, fileName));
}

function loadMcpToolsManifest(): JsonMcpToolsManifest {
    return readResourceJson<JsonMcpToolsManifest>('mcp-tools.json') ?? {};
}

function loadInstructionsFilesMetadata(): JsonInstructionsFilesMetadata {
    return readResourceJson<JsonInstructionsFilesMetadata>('instructions-files.metadata.json') ?? {};
}

function loadPackageManifest(): JsonPackageManifest {
    return readJsonFromPath<JsonPackageManifest>(PACKAGE_JSON_PATH) ?? {};
}

/**
 * Extracts file extensions (with leading dot, lowercased) from an
 * `applyTo` glob string. Handles brace expansion and comma-separated
 * globs uniformly: `**\/*.{cs,fs,vb}` → `[.cs, .fs, .vb]`,
 * `**\/*.cs,**\/*.ts` → `[.cs, .ts]`. Globs without a `*.<ext>` segment
 * (e.g. `**\/Dockerfile*`) yield nothing — those route via filename,
 * which we don't currently match.
 *
 * NOTE: `applyTo` may use brace expansion that contains commas
 * (`*.{cs,fs}`), so the input string is NOT pre-split on commas.
 * The two regexes are non-overlapping: `\*\.([A-Za-z0-9]+)` won't
 * match `*.{` because `{` isn't in the character class.
 */
function extractExtensions(applyTo: string): readonly string[] {
    const result = new Set<string>();

    // Match `*.{a,b,c}` brace groups anywhere in the string.
    const brace = /\*\.\{([^}]+)\}/g;
    let m: RegExpExecArray | null;
    while ((m = brace.exec(applyTo)) !== null) {
        for (const part of m[1].split(',')) {
            const ext = part.trim().toLowerCase();
            if (ext.length > 0) {
                result.add(`.${ext}`);
            }
        }
    }

    // Match bare `*.ext` segments (won't overlap with brace groups).
    const bare = /\*\.([A-Za-z0-9]+)\b/g;
    while ((m = bare.exec(applyTo)) !== null) {
        result.add(`.${m[1].toLowerCase()}`);
    }

    return [...result];
}

function buildRouteIndex(): RouteIndex {
    const mcp = loadMcpToolsManifest();
    const instr = loadInstructionsFilesMetadata();
    const pkg = loadPackageManifest();

    // Tool name -> categories. Used to invert into category -> tools.
    const categoryToTools = new Map<string, string[]>();
    const mcpToolNames: string[] = [];
    for (const tool of mcp.tools ?? []) {
        mcpToolNames.push(tool.name);
        for (const category of tool.categories ?? []) {
            const key = category.toLowerCase();
            const list = categoryToTools.get(key);
            if (list) {
                list.push(tool.name);
            } else {
                categoryToTools.set(key, [tool.name]);
            }
        }
    }

    // Extension -> instruction file names that target that extension via applyTo.
    const extToInstructions = new Map<string, string[]>();
    for (const file of instr.instructions ?? []) {
        if (!file.applyTo) {
            continue;
        }
        for (const ext of extractExtensions(file.applyTo)) {
            const list = extToInstructions.get(ext);
            if (list) {
                list.push(file.fileName);
            } else {
                extToInstructions.set(ext, [file.fileName]);
            }
        }
    }

    const lmToolNames = (pkg.contributes?.languageModelTools ?? []).map(t => t.name);

    return { categoryToTools, extToInstructions, mcpToolNames, lmToolNames };
}

/** Word-boundary-ish literal match that tolerates non-`\w` chars in the
 *  needle (e.g. `C#`, `.NET`). Asserts the surrounding chars are non-word
 *  in the prompt itself, so `c#` matches in `"port to c#"` but not in
 *  `"abc#def"`. */
function escapeRegex(literal: string): string {
    return literal.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function literalMatchesPrompt(prompt: string, needle: string): boolean {
    const re = new RegExp(`(?:^|[^A-Za-z0-9_])${escapeRegex(needle)}(?=$|[^A-Za-z0-9_])`, 'i');
    return re.test(prompt);
}

interface PromptMatch {
    readonly tools: readonly string[];
    readonly instructions: readonly string[];
    readonly matchedCategories: readonly string[];
    readonly matchedExtensions: readonly string[];
}

function matchPrompt(prompt: string, index: RouteIndex): PromptMatch | null {
    const tools = new Set<string>();
    const matchedCategories: string[] = [];
    for (const [category, toolNames] of index.categoryToTools) {
        if (literalMatchesPrompt(prompt, category)) {
            matchedCategories.push(category);
            for (const t of toolNames) {
                tools.add(t);
            }
        }
    }

    // Find file extensions in the prompt. Pattern: a `.` followed by
    // 1–12 alphanumerics, ended by a non-word boundary (or end of
    // string). Crucially we do NOT require a non-word char before the
    // dot, so filename mentions like `Foo.cs`, `foo.csproj`, or
    // `Module.psm1` extract the extension. The cost is matching
    // `.example` in `host.example.com` — harmless because such
    // pseudo-extensions are not in `extToInstructions`.
    const extInPrompt = new Set<string>();
    const extRe = /\.([A-Za-z][A-Za-z0-9]{0,12})(?=$|[^A-Za-z0-9_])/g;
    let m: RegExpExecArray | null;
    while ((m = extRe.exec(prompt)) !== null) {
        extInPrompt.add(`.${m[1].toLowerCase()}`);
    }

    const instructions = new Set<string>();
    const matchedExtensions: string[] = [];
    for (const ext of extInPrompt) {
        const list = index.extToInstructions.get(ext);
        if (list) {
            matchedExtensions.push(ext);
            for (const f of list) {
                instructions.add(f);
            }
        }
    }

    if (tools.size === 0 && instructions.size === 0) {
        return null;
    }
    return {
        tools: [...tools],
        instructions: [...instructions],
        matchedCategories,
        matchedExtensions,
    };
}

function renderStaticBlock(
    lmToolNames: readonly string[],
    mcpToolNames: readonly string[],
): string {
    const lmList = lmToolNames.length === 0
        ? '(no LM tools detected)'
        : lmToolNames.map(t => `\`${t}\``).join(', ');
    const mcpList = mcpToolNames.length === 0
        ? '(no MCP tools detected)'
        : mcpToolNames.map(t => `\`${t}\``).join(', ');

    return [
        '<!-- AutoContext UserPromptSubmit: discovery -->',
        '',
        'This workspace ships AutoContext tooling. Before answering, consider whether any of the following apply to this turn — and if so, **call them**, don\'t paraphrase them:',
        '',
        `- LM instructions tools: ${lmList}.`,
        `- MCP codebase tools: ${mcpList}.`,
        '- Re-anchor on the AutoContext meta-instructions injected at session start (`copilot.instructions.md`, `autocontext.instructions.md`); they still apply to this turn.',
    ].join('\n');
}

function renderRoutedBlock(match: PromptMatch): string {
    const triggerParts: string[] = [];
    if (match.matchedCategories.length > 0) {
        triggerParts.push(`category ${match.matchedCategories.map(c => `\`${c}\``).join(', ')}`);
    }
    if (match.matchedExtensions.length > 0) {
        triggerParts.push(`extension ${match.matchedExtensions.map(e => `\`${e}\``).join(', ')}`);
    }
    const trigger = triggerParts.join(' and ');

    const lines: string[] = [
        '<!-- AutoContext UserPromptSubmit: routed -->',
        '',
        `This turn matches: ${trigger}.`,
    ];

    if (match.tools.length > 0) {
        lines.push('');
        lines.push(`Strongly relevant MCP tools: ${match.tools.map(t => `\`${t}\``).join(', ')}.`);
    }

    if (match.instructions.length > 0) {
        lines.push('');
        lines.push(`Strongly relevant instruction files: ${match.instructions.map(n => `\`${n}\``).join(', ')}.`);
        lines.push('');
        lines.push(
            `Use \`get_autocontext_instructions_file\` to fetch any of them before writing code, and run the MCP tools their \`## MCP Tool Validation\` sections name on every file you write.`,
        );
    }

    return lines.join('\n');
}

async function main(): Promise<void> {
    const raw = await drainStdin();
    const input = parseInput(raw);
    const prompt = (input.prompt ?? '').trim();

    const index = buildRouteIndex();

    const sections: string[] = [];
    sections.push(renderStaticBlock(index.lmToolNames, index.mcpToolNames));

    if (prompt.length > 0) {
        const match = matchPrompt(prompt, index);
        if (match) {
            sections.push(renderRoutedBlock(match));
        }
    }

    const output: UserPromptSubmitOutput = {
        hookSpecificOutput: {
            hookEventName: 'UserPromptSubmit',
            additionalContext: sections.join('\n\n'),
        },
    };

    process.stdout.write(`${JSON.stringify(output)}\n`);
}

void main();
