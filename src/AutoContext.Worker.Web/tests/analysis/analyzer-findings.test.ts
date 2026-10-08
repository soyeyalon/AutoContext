import { describe, it, expect } from 'vitest';
import { AnalyzerFindings } from '#src/analysis/analyzer-findings.js';

describe('AnalyzerFindings', () => {
    it('reports a pass when there are no findings', () => {
        const output = new AnalyzerFindings().toOutput('TypeScript coding style is correct.', 'TypeScript coding style');

        expect.soft(output.passed).toBe(true);
        expect.soft(output.report).toBe('✅ TypeScript coding style is correct.');
        expect.soft(output.findings).toEqual([]);
    });

    it('numbers violations in line order and carries them as structured findings', () => {
        const findings = new AnalyzerFindings();
        findings.add('lang-typescript#INST0011', 9, 'No enums.');
        findings.add('lang-typescript#INST0005', 2, 'No any.');

        const output = findings.toOutput('ok.', 'TypeScript coding style');

        expect.soft(output.passed).toBe(false);
        expect.soft(output.report).toBe(
            '❌ Found 2 TypeScript coding style violation(s):\n  1. Line 2: No any.\n  2. Line 9: No enums.',
        );
        expect.soft(output.findings[0]).toEqual({
            ruleId: 'lang-typescript#INST0011', severity: 'violation', line: 9, message: 'No enums.',
        });
    });

    it('lists suggestions without failing the check', () => {
        const findings = new AnalyzerFindings();
        findings.suggest('lang-typescript#INST0018', 4, 'Avoid as.');

        const output = findings.toOutput('ok.', 'TypeScript coding style');

        expect.soft(output.passed).toBe(true);
        expect.soft(output.report).toBe(
            '✅ ok.\n💡 1 optional suggestion(s) — these do not fail the check:\n  1. Line 4: Avoid as.',
        );
    });

    it('omits the line of a whole-file finding', () => {
        const findings = new AnalyzerFindings();
        findings.add('lang-typescript#INST0005', undefined, 'File-level.');

        expect(findings.toOutput('ok.', 'x').findings[0]).toEqual({
            ruleId: 'lang-typescript#INST0005', severity: 'violation', message: 'File-level.',
        });
    });

    it('rejects a finding with no rule id', () => {
        expect(() => new AnalyzerFindings().add(' ', 1, 'message')).toThrow(/rule id/);
    });
});
