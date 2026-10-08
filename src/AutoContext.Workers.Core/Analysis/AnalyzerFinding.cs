namespace AutoContext.Workers.Core.Analysis;

/// <summary>
/// One structured finding from an analyzer task: the rule it enforces, how
/// much weight it carries, where it is, and what is wrong.
/// </summary>
/// <param name="RuleId">The rule the finding enforces, as
/// <c>&lt;instructions-file-key&gt;#INST####</c> (for example
/// <c>lang-csharp#INST0005</c>) — the same form an instructions file uses to
/// reference a rule — or <c>editorconfig#&lt;key&gt;</c> for a check driven
/// by an <c>.editorconfig</c> setting rather than an instruction rule.</param>
/// <param name="Severity">Whether the finding fails the check.</param>
/// <param name="Line">The one-based line the finding is on, or
/// <see langword="null"/> when it concerns the whole file.</param>
/// <param name="Message">What is wrong and how to fix it. Carries no line
/// number, so the same problem reads the same wherever it moves.</param>
public sealed record AnalyzerFinding(
    string RuleId,
    AnalyzerFindingSeverity Severity,
    int? Line,
    string Message);
