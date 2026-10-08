namespace AutoContext.Workers.Core.Analysis;

/// <summary>
/// How much weight an analyzer finding carries. Only a
/// <see cref="Violation"/> fails a check; a <see cref="Suggestion"/> is
/// reported so the agent can see it, but never makes the check fail.
/// </summary>
public enum AnalyzerFindingSeverity
{
    /// <summary>The code breaks a rule the check gates on; the check fails.</summary>
    Violation,

    /// <summary>The code departs from a preference that is a matter of
    /// taste or context; reported, but the check still passes.</summary>
    Suggestion,
}
