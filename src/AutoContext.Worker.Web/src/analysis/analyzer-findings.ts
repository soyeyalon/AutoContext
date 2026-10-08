/**
 * Whether a finding fails the check (`violation`) or is reported without
 * failing it (`suggestion`).
 */
export type AnalyzerFindingSeverity = 'violation' | 'suggestion';

/**
 * One structured analyzer finding. Mirrors `AnalyzerFinding` in the .NET
 * `AutoContext.Workers.Core.Analysis` namespace so every worker reports the
 * same shape.
 */
export interface AnalyzerFinding {
    /**
     * The rule enforced, as `<instructions-file-key>#INST####` (e.g.
     * `lang-typescript#INST0005`) — the form an instructions file uses to
     * cite a rule.
     */
    readonly ruleId: string;
    readonly severity: AnalyzerFindingSeverity;
    /** One-based line, or `undefined` when the finding concerns the whole file. */
    readonly line?: number;
    /** What is wrong and how to fix it; carries no line number. */
    readonly message: string;
}

/** The analyzer task output every worker returns. */
export interface AnalyzerOutput {
    readonly passed: boolean;
    readonly report: string;
    readonly findings: readonly AnalyzerFinding[];
}

/**
 * Collects an analyzer task's findings and renders the task output
 * `{ passed, report, findings }`. `passed` is false only when there is at
 * least one violation; suggestions are listed under their own heading and
 * never fail the check. The report text matches the .NET workers' format.
 */
export class AnalyzerFindings {
    private readonly items: AnalyzerFinding[] = [];

    get passed(): boolean {
        return !this.items.some(finding => finding.severity === 'violation');
    }

    get findings(): readonly AnalyzerFinding[] {
        return this.items;
    }

    add(ruleId: string, line: number | undefined, message: string): void {
        this.push({ ruleId, severity: 'violation', line, message });
    }

    suggest(ruleId: string, line: number | undefined, message: string): void {
        this.push({ ruleId, severity: 'suggestion', line, message });
    }

    renderReport(passText: string, violationNoun: string): string {
        const violations = this.ordered('violation');
        const suggestions = this.ordered('suggestion');
        let report = violations.length === 0
            ? `✅ ${passText}`
            : `❌ Found ${violations.length} ${violationNoun} violation(s):${AnalyzerFindings.numbered(violations)}`;

        if (suggestions.length > 0) {
            report += `\n💡 ${suggestions.length} optional suggestion(s) — these do not fail the check:${AnalyzerFindings.numbered(suggestions)}`;
        }

        return report;
    }

    /**
     * Renders the task output. When `request` carries `disabledRules`,
     * findings they switch off are left out — a rule id
     * (`lang-typescript#INST0018`) drops that rule, a bare instructions-file
     * key (`lang-typescript`) drops every rule of that file — so a check
     * never reports a rule the user turned off.
     */
    toOutput(passText: string, violationNoun: string, request?: Record<string, unknown>): AnalyzerOutput {
        const disabled = AnalyzerFindings.readDisabledRules(request);

        if (disabled.size > 0) {
            const kept = new AnalyzerFindings();
            kept.items.push(...this.items.filter(finding => !AnalyzerFindings.isDisabled(finding.ruleId, disabled)));
            return kept.toOutput(passText, violationNoun);
        }

        return {
            passed: this.passed,
            report: this.renderReport(passText, violationNoun),
            findings: this.items.map(finding => finding.line === undefined
                ? { ruleId: finding.ruleId, severity: finding.severity, message: finding.message }
                : { ...finding }),
        };
    }

    private static readDisabledRules(request: Record<string, unknown> | undefined): ReadonlySet<string> {
        const rules = request?.['disabledRules'];
        return new Set(Array.isArray(rules) ? rules.filter((rule): rule is string => typeof rule === 'string' && rule.length > 0) : []);
    }

    private static isDisabled(ruleId: string, disabled: ReadonlySet<string>): boolean {
        const separator = ruleId.indexOf('#');
        return disabled.has(ruleId) || (separator > 0 && disabled.has(ruleId.slice(0, separator)));
    }

    private push(finding: AnalyzerFinding): void {
        if (finding.ruleId.trim().length === 0 || finding.message.trim().length === 0) {
            throw new Error('A finding needs a rule id and a message.');
        }

        this.items.push(finding);
    }

    private ordered(severity: AnalyzerFindingSeverity): AnalyzerFinding[] {
        return this.items
            .filter(finding => finding.severity === severity)
            .sort((a, b) => (a.line ?? 0) - (b.line ?? 0));
    }

    private static numbered(findings: readonly AnalyzerFinding[]): string {
        return findings
            .map((finding, index) => `\n  ${index + 1}. ${finding.line === undefined ? '' : `Line ${finding.line}: `}${finding.message}`)
            .join('');
    }
}
