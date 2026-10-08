import ts from 'typescript';
import type { McpTask } from '#types/mcp-task.js';
import { AnalyzerFindings } from '../../analysis/analyzer-findings.js';

const PASS_TEXT = 'TypeScript coding style is correct.';
const VIOLATION_NOUN = 'TypeScript coding style';

/** How each source extension is parsed; anything else is not analysed. */
const SCRIPT_KINDS: Readonly<Record<string, ts.ScriptKind>> = {
    '.ts': ts.ScriptKind.TS,
    '.mts': ts.ScriptKind.TS,
    '.cts': ts.ScriptKind.TS,
    '.tsx': ts.ScriptKind.TSX,
    '.js': ts.ScriptKind.JS,
    '.mjs': ts.ScriptKind.JS,
    '.cjs': ts.ScriptKind.JS,
    '.jsx': ts.ScriptKind.JSX,
};

/**
 * `analyze_typescript_coding_style` — enforces coding-style rules from
 * `lang-typescript.instructions.md`: no `any`, no enums, no `@ts-ignore`,
 * no `as` type assertions, no non-null assertions, no unconstrained
 * generics, no `Function`/`Object`/`{}` types, prefer `interface` over
 * `type` for object shapes, and explicit return types on exported
 * functions. Each finding names the rule it enforces.
 *
 * The source is parsed by its file kind (`filePath`, legacy `originalPath`):
 * `.tsx` and `.jsx` with JSX, JavaScript without the rules that need type
 * annotations it cannot have. Files of any other kind — a `.vue` or
 * `.svelte` component, say — are not TypeScript source and are not analysed.
 * Without a path the source is treated as TypeScript.
 *
 * Rules that are a matter of taste rather than correctness — type
 * assertions, non-null assertions, unconstrained generics, `enum`, and
 * `type` aliases for object shapes — are reported as suggestions and do
 * not fail the check.
 *
 * Request `data`:   `{ "content": "<typescript-source>", "filePath": "<abs-path>" }`
 * Response `output`: `{ "passed": <bool>, "report": "<text>", "findings": [...] }`
 */
export class AnalyzeTypeScriptCodingStyleTask implements McpTask {
    readonly taskName = 'analyze_typescript_coding_style';

    async execute(
        data: Record<string, unknown>,
        signal: AbortSignal,
    ): Promise<unknown> {
        signal.throwIfAborted();

        const content = data['content'];
        if (typeof content !== 'string') {
            throw new Error("'data.content' is required and must be a string.");
        }
        if (content.trim().length === 0) {
            throw new Error("'data.content' must not be empty or whitespace.");
        }

        const filePath = AnalyzeTypeScriptCodingStyleTask.readPath(data);
        const extension = filePath === undefined ? '.ts' : AnalyzeTypeScriptCodingStyleTask.extensionOf(filePath);
        const scriptKind = SCRIPT_KINDS[extension];

        if (scriptKind === undefined) {
            return new AnalyzerFindings().toOutput(
                `Nothing to check: '${extension || filePath}' files are not TypeScript or JavaScript source.`,
                VIOLATION_NOUN,
            );
        }

        const findings = AnalyzeTypeScriptCodingStyleTask.analyze(content, extension, scriptKind);
        signal.throwIfAborted();

        return findings.toOutput(PASS_TEXT, VIOLATION_NOUN, data);
    }

    private static readPath(data: Record<string, unknown>): string | undefined {
        for (const key of ['filePath', 'originalPath']) {
            const value = data[key];
            if (typeof value === 'string' && value.trim().length > 0) {
                return value;
            }
        }
        return undefined;
    }

    private static extensionOf(filePath: string): string {
        const name = filePath.replace(/\\/g, '/').split('/').pop() ?? '';
        const dot = name.lastIndexOf('.');
        return dot <= 0 ? '' : name.slice(dot).toLowerCase();
    }

    private static isTypeScript(sourceFile: ts.SourceFile): boolean {
        return !/\.(?:m|c)?jsx?$/.test(sourceFile.fileName);
    }

    private static lineOf(sourceFile: ts.SourceFile, pos: number): number {
        return sourceFile.getLineAndCharacterOfPosition(pos).line + 1;
    }

    private static hasExportModifier(node: ts.Node): boolean {
        if (!ts.canHaveModifiers(node)) return false;
        const modifiers = ts.getModifiers(node);
        return modifiers?.some(m => m.kind === ts.SyntaxKind.ExportKeyword) ?? false;
    }

    /**
     * Returns true when a type node represents the `const` keyword used in
     * an `as const` assertion. Uses the AST shape rather than `getText()`
     * so the check is self-documenting and independent of source-text
     * representation.
     */
    private static isConstAssertion(typeNode: ts.TypeNode): boolean {
        return ts.isTypeReferenceNode(typeNode)
            && ts.isIdentifier(typeNode.typeName)
            && typeNode.typeName.text === 'const';
    }

    // @ts-ignore lives in comments, which are not AST nodes, so it is found by pattern.
    private static checkTsIgnoreComments(sourceFile: ts.SourceFile, findings: AnalyzerFindings): void {
        const text = sourceFile.getFullText();
        const pattern = /\/\/\s*@ts-ignore\b/g;
        let match: RegExpExecArray | null;
        while ((match = pattern.exec(text)) !== null) {
            findings.add(
                'lang-typescript#INST0004',
                AnalyzeTypeScriptCodingStyleTask.lineOf(sourceFile, match.index),
                'Use `// @ts-expect-error` instead of `// @ts-ignore` — it errors when the suppression is no longer needed.',
            );
        }
    }

    private static analyze(content: string, extension: string, scriptKind: ts.ScriptKind): AnalyzerFindings {
        const sourceFile = ts.createSourceFile(
            `input${extension}`,
            content,
            ts.ScriptTarget.Latest,
            true,
            scriptKind,
        );

        const findings = new AnalyzerFindings();
        AnalyzeTypeScriptCodingStyleTask.checkTsIgnoreComments(sourceFile, findings);
        AnalyzeTypeScriptCodingStyleTask.visit(sourceFile, sourceFile, findings);
        return findings;
    }

    private static visit(node: ts.Node, sourceFile: ts.SourceFile, findings: AnalyzerFindings): void {
        const lineOf = AnalyzeTypeScriptCodingStyleTask.lineOf;

        if (ts.isEnumDeclaration(node)) {
            findings.suggest(
                'lang-typescript#INST0011',
                lineOf(sourceFile, node.getStart(sourceFile)),
                'Prefer a `const` object with `as const` and a derived union type instead of `enum`.',
            );
        }

        if (node.kind === ts.SyntaxKind.AnyKeyword) {
            findings.add(
                'lang-typescript#INST0005',
                lineOf(sourceFile, node.getStart(sourceFile)),
                'Avoid `any` — use `unknown` for untyped inputs, proper types for known shapes, or generics for parameterized behavior.',
            );
        }

        if (ts.isTypeReferenceNode(node)) {
            const name = node.typeName.getText(sourceFile);
            if (name === 'Function') {
                findings.add(
                    'lang-typescript#INST0012',
                    lineOf(sourceFile, node.getStart(sourceFile)),
                    'Avoid `Function` as a type — use a specific function signature instead.',
                );
            } else if (name === 'Object') {
                findings.add(
                    'lang-typescript#INST0012',
                    lineOf(sourceFile, node.getStart(sourceFile)),
                    'Avoid `Object` as a type — use `object`, `Record<string, unknown>`, or a specific interface.',
                );
            }
        }

        if (ts.isTypeLiteralNode(node) && node.members.length === 0) {
            findings.add(
                'lang-typescript#INST0012',
                lineOf(sourceFile, node.getStart(sourceFile)),
                'Avoid `{}` as a type — it matches any non-nullish value; use `object` or `Record<string, unknown>` instead.',
            );
        }

        if (
            ts.isTypeAliasDeclaration(node) &&
            ts.isTypeLiteralNode(node.type) &&
            node.type.members.length > 0
        ) {
            const name = node.name.getText(sourceFile);
            findings.suggest(
                'lang-typescript#INST0010',
                lineOf(sourceFile, node.getStart(sourceFile)),
                `Use \`interface ${name}\` instead of \`type ${name} = { ... }\` for object shapes.`,
            );
        }

        // Return-type annotations exist only in TypeScript; JavaScript cannot satisfy them.
        const typed = AnalyzeTypeScriptCodingStyleTask.isTypeScript(sourceFile);

        if (
            typed &&
            ts.isFunctionDeclaration(node) &&
            AnalyzeTypeScriptCodingStyleTask.hasExportModifier(node) &&
            !node.type
        ) {
            const name = node.name?.getText(sourceFile) ?? '<anonymous>';
            findings.add(
                'lang-typescript#INST0006',
                lineOf(sourceFile, node.getStart(sourceFile)),
                `Exported function \`${name}\` should have an explicit return type annotation.`,
            );
        }

        if (typed && ts.isVariableStatement(node) && AnalyzeTypeScriptCodingStyleTask.hasExportModifier(node)) {
            for (const decl of node.declarationList.declarations) {
                const init = decl.initializer;
                if (init && (ts.isArrowFunction(init) || ts.isFunctionExpression(init)) && !init.type) {
                    const name = decl.name.getText(sourceFile);
                    findings.add(
                        'lang-typescript#INST0006',
                        lineOf(sourceFile, decl.getStart(sourceFile)),
                        `Exported function \`${name}\` should have an explicit return type annotation.`,
                    );
                }
            }
        }

        if (typed && ts.isExportAssignment(node) && !node.isExportEquals) {
            const expr = node.expression;
            if ((ts.isArrowFunction(expr) || ts.isFunctionExpression(expr)) && !expr.type) {
                findings.add(
                    'lang-typescript#INST0006',
                    lineOf(sourceFile, node.getStart(sourceFile)),
                    'Default-exported function should have an explicit return type annotation.',
                );
            }
        }

        if (ts.isTypeParameterDeclaration(node) && !node.constraint) {
            const name = node.name.getText(sourceFile);
            findings.suggest(
                'lang-typescript#INST0009',
                lineOf(sourceFile, node.getStart(sourceFile)),
                `Generic type parameter \`${name}\` should be constrained with \`extends\`.`,
            );
        }

        if (ts.isAsExpression(node) && !AnalyzeTypeScriptCodingStyleTask.isConstAssertion(node.type)) {
            findings.suggest(
                'lang-typescript#INST0018',
                lineOf(sourceFile, node.getStart(sourceFile)),
                'Avoid type assertions (`as`) — narrow with `typeof`, `instanceof`, `in`, or type guards instead.',
            );
        }

        if (ts.isNonNullExpression(node)) {
            findings.suggest(
                'lang-typescript#INST0019',
                lineOf(sourceFile, node.getStart(sourceFile)),
                'Avoid non-null assertions (`!`) — verify nullability with a proper check or type guard.',
            );
        }

        ts.forEachChild(node, child => AnalyzeTypeScriptCodingStyleTask.visit(child, sourceFile, findings));
    }
}
