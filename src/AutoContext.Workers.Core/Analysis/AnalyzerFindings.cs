namespace AutoContext.Workers.Core.Analysis;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Collects an analyzer task's findings and renders them as the task's
/// output: <c>{ "passed", "report", "findings" }</c>. <c>passed</c> is
/// <see langword="true"/> unless there is at least one
/// <see cref="AnalyzerFindingSeverity.Violation"/>; <c>report</c> is the
/// human- and agent-readable text; <c>findings</c> is the same content as
/// structured records, one per finding, so a caller can filter, diff, or
/// count them without parsing prose.
/// </summary>
/// <remarks>
/// The report text keeps the shape agents already read: a <c>✅</c> line
/// when nothing gates, or <c>❌ Found N … violation(s):</c> followed by a
/// numbered list. Suggestions follow under their own heading and never
/// change <c>passed</c>.
/// </remarks>
public sealed class AnalyzerFindings
{
    private readonly List<AnalyzerFinding> _items = [];

    /// <summary>Gets the findings collected so far, in the order they were added.</summary>
    public IReadOnlyList<AnalyzerFinding> Items => _items;

    /// <summary>Gets a value indicating whether no finding fails the check.</summary>
    public bool Passed => !_items.Any(static finding => finding.Severity == AnalyzerFindingSeverity.Violation);

    /// <summary>Records a finding that fails the check.</summary>
    /// <param name="ruleId">The rule enforced (see <see cref="AnalyzerFinding.RuleId"/>).</param>
    /// <param name="line">The one-based line, or <see langword="null"/> for the whole file.</param>
    /// <param name="message">What is wrong, without a line number.</param>
    public void Add(string ruleId, int? line, string message)
        => Add(ruleId, AnalyzerFindingSeverity.Violation, line, message);

    /// <summary>Records a finding that is reported but does not fail the check.</summary>
    /// <param name="ruleId">The rule enforced (see <see cref="AnalyzerFinding.RuleId"/>).</param>
    /// <param name="line">The one-based line, or <see langword="null"/> for the whole file.</param>
    /// <param name="message">What is wrong, without a line number.</param>
    public void Suggest(string ruleId, int? line, string message)
        => Add(ruleId, AnalyzerFindingSeverity.Suggestion, line, message);

    /// <summary>Records a finding with an explicit severity.</summary>
    /// <param name="ruleId">The rule enforced (see <see cref="AnalyzerFinding.RuleId"/>).</param>
    /// <param name="severity">Whether the finding fails the check.</param>
    /// <param name="line">The one-based line, or <see langword="null"/> for the whole file.</param>
    /// <param name="message">What is wrong, without a line number.</param>
    /// <exception cref="ArgumentException"><paramref name="ruleId"/> or
    /// <paramref name="message"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public void Add(string ruleId, AnalyzerFindingSeverity severity, int? line, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _items.Add(new AnalyzerFinding(ruleId, severity, line, message));
    }

    /// <summary>
    /// Renders the report text for these findings.
    /// </summary>
    /// <param name="passText">The sentence shown when nothing gates (e.g.
    /// <c>Code style is correct.</c>).</param>
    /// <param name="violationNoun">The noun naming this check's violations
    /// (e.g. <c>style</c> in <c>Found 2 style violation(s)</c>).</param>
    /// <returns>The report text.</returns>
    public string RenderReport(string passText, string violationNoun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passText);
        ArgumentException.ThrowIfNullOrWhiteSpace(violationNoun);

        var violations = _items.Where(static finding => finding.Severity == AnalyzerFindingSeverity.Violation).ToList();
        var suggestions = _items.Where(static finding => finding.Severity == AnalyzerFindingSeverity.Suggestion).ToList();
        var builder = new StringBuilder();

        if (violations.Count == 0)
        {
            builder.Append("✅ ").Append(passText);
        }
        else
        {
            builder.Append("❌ Found ").Append(violations.Count).Append(' ').Append(violationNoun).Append(" violation(s):");
            AppendNumbered(builder, violations);
        }

        if (suggestions.Count > 0)
        {
            builder.Append("\n💡 ").Append(suggestions.Count).Append(" optional suggestion(s) — these do not fail the check:");
            AppendNumbered(builder, suggestions);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders the task output <c>{ "passed", "report", "findings" }</c>.
    /// </summary>
    /// <param name="passText">The sentence shown when nothing gates.</param>
    /// <param name="violationNoun">The noun naming this check's violations.</param>
    /// <returns>The output element.</returns>
    public JsonElement ToOutput(string passText, string violationNoun)
    {
        var findings = new JsonArray();

        foreach (var finding in _items)
        {
            var node = new JsonObject
            {
                ["ruleId"] = finding.RuleId,
                ["severity"] = finding.Severity == AnalyzerFindingSeverity.Violation ? "violation" : "suggestion",
            };

            if (finding.Line is { } line)
            {
                node["line"] = line;
            }

            node["message"] = finding.Message;
            findings.Add(node);
        }

        var output = new JsonObject
        {
            ["passed"] = Passed,
            ["report"] = RenderReport(passText, violationNoun),
            ["findings"] = findings,
        };

        return JsonSerializer.SerializeToElement(output);
    }

    private static void AppendNumbered(StringBuilder builder, List<AnalyzerFinding> findings)
    {
        for (var index = 0; index < findings.Count; index++)
        {
            var finding = findings[index];
            builder.Append("\n  ").Append(index + 1).Append(". ");

            if (finding.Line is { } line)
            {
                builder.Append("Line ").Append(line).Append(": ");
            }

            builder.Append(finding.Message);
        }
    }
}
