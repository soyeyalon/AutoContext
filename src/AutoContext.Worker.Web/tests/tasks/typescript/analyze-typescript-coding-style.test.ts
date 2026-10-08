import { describe, it, expect } from 'vitest';
import { AnalyzeTypeScriptCodingStyleTask } from '#src/tasks/typescript/analyze-typescript-coding-style.js';

interface TaskResult {
    readonly passed: boolean;
    readonly report: string;
}

async function run(content: string): Promise<TaskResult> {
    const task = new AnalyzeTypeScriptCodingStyleTask();
    const controller = new AbortController();
    const result = await task.execute({ content }, controller.signal);
    return result as TaskResult;
}

describe('AnalyzeTypeScriptCodingStyleTask', () => {
    it('has the manifest task name', () => {
        expect(new AnalyzeTypeScriptCodingStyleTask().taskName).toBe(
            'analyze_typescript_coding_style',
        );
    });

    it('passes clean TypeScript', async () => {
        const { passed, report } = await run(`
            interface User {
                name: string;
                age: number;
            }

            function greet(user: User): string {
                return \`Hello, \${user.name}\`;
            }
        `);
        expect.soft(passed).toBe(true);
        expect.soft(report).toMatch(/^✅/);
    });

    it('flags any', async () => {
        const { passed, report } = await run(`
            function process(data: any): void {
                console.log(data);
            }
        `);
        expect.soft(passed).toBe(false);
        expect.soft(report).toMatch(/^❌/);
        expect.soft(report).toContain('any');
    });

    it('suggests replacing enum without failing the check', async () => {
        const { passed, report } = await run(`
            enum Direction { Up, Down }
        `);
        expect.soft(passed).toBe(true);
        expect.soft(report).toContain('optional suggestion');
        expect.soft(report).toContain('enum');
    });

    it('flags @ts-ignore', async () => {
        const { passed, report } = await run(`
            // @ts-ignore
            const x = 1 + '2';
        `);
        expect.soft(passed).toBe(false);
        expect.soft(report).toContain('@ts-expect-error');
    });

    it('flags Function and Object type references', async () => {
        const { report } = await run(`
            function a(cb: Function): void { cb(); }
            function b(o: Object): void { console.log(o); }
        `);
        expect.soft(report).toContain('Function');
        expect.soft(report).toContain('Object');
    });

    it('flags {} empty type literal', async () => {
        const { report } = await run(`
            function process(data: {}): void { console.log(data); }
        `);
        expect.soft(report).toContain('{}');
    });

    it('suggests narrowing over as type assertions but not as const', async () => {
        const { passed: passedAs, report: withAs } = await run(`
            declare const x: unknown;
            const y = x as string;
        `);
        expect.soft(passedAs).toBe(true);
        expect.soft(withAs).toContain('optional suggestion');
        expect.soft(withAs).toContain('as');

        const { passed: passedConst } = await run(`
            const tuple = [1, 2, 3] as const;
        `);
        expect.soft(passedConst).toBe(true);
    });

    it('flags non-null assertions', async () => {
        const { report } = await run(`
            declare const x: string | undefined;
            const y = x!;
        `);
        expect.soft(report).toContain('non-null');
    });

    it('flags unconstrained generics', async () => {
        const { report } = await run(`
            function identity<T>(x: T): T { return x; }
        `);
        expect.soft(report).toContain('extends');
    });

    it('flags exported function without return type', async () => {
        const { report } = await run(`
            export function add(a: number, b: number) {
                return a + b;
            }
        `);
        expect.soft(report).toContain('return type');
    });

    it('reports each finding with the rule it enforces', async () => {
        const task = new AnalyzeTypeScriptCodingStyleTask();
        const output = await task.execute({ content: 'const x: any = 1;\n' }, new AbortController().signal) as {
            readonly findings: readonly { readonly ruleId: string; readonly line?: number }[];
        };

        expect(output.findings).toEqual([
            expect.objectContaining({ ruleId: 'lang-typescript#INST0005', line: 1 }),
        ]);
    });

    it('parses .tsx with JSX', async () => {
        const task = new AnalyzeTypeScriptCodingStyleTask();
        const output = await task.execute(
            { content: 'export function Hello(): JSX.Element { return <div>hi</div>; }\n', filePath: '/app/Hello.tsx' },
            new AbortController().signal,
        ) as TaskResult;

        expect(output.passed).toBe(true);
    });

    it('does not ask JavaScript for return-type annotations it cannot have', async () => {
        const task = new AnalyzeTypeScriptCodingStyleTask();
        const output = await task.execute(
            { content: 'export function add(a, b) { return a + b; }\n', filePath: '/app/math.js' },
            new AbortController().signal,
        ) as TaskResult;

        expect.soft(output.passed).toBe(true);
        expect.soft(output.report).not.toContain('return type');
    });

    it('still asks TypeScript for return-type annotations', async () => {
        const task = new AnalyzeTypeScriptCodingStyleTask();
        const output = await task.execute(
            { content: 'export function add(a: number, b: number) { return a + b; }\n', filePath: '/app/math.ts' },
            new AbortController().signal,
        ) as TaskResult;

        expect.soft(output.passed).toBe(false);
        expect.soft(output.report).toContain('return type');
    });

    it('does not analyse files that are not TypeScript or JavaScript source', async () => {
        const task = new AnalyzeTypeScriptCodingStyleTask();
        const output = await task.execute(
            { content: '<template><div /></template>\n<script setup lang="ts">const x: any = 1;</script>\n', filePath: 'C:\\app\\Widget.vue' },
            new AbortController().signal,
        ) as TaskResult;

        expect.soft(output.passed).toBe(true);
        expect.soft(output.report).toContain('.vue');
    });

    it('throws when data.content is missing', async () => {
        const task = new AnalyzeTypeScriptCodingStyleTask();
        await expect(task.execute({}, new AbortController().signal)).rejects.toThrow(
            /'data\.content' is required/,
        );
    });

    it('throws when data.content is not a string', async () => {
        const task = new AnalyzeTypeScriptCodingStyleTask();
        await expect(
            task.execute({ content: 123 }, new AbortController().signal),
        ).rejects.toThrow(/'data\.content' is required/);
    });

    it('throws when data.content is empty/whitespace', async () => {
        const task = new AnalyzeTypeScriptCodingStyleTask();
        await expect(
            task.execute({ content: '   \n  ' }, new AbortController().signal),
        ).rejects.toThrow(/must not be empty/);
    });
});
