namespace AutoContext.Workers.Core.Tests.Analysis;

using System.Text.Json;

using AutoContext.Workers.Core.Analysis;

public sealed class AnalyzerFindingsTests
{
    public sealed class RenderReport
    {
        [Fact]
        public void Should_report_a_pass_when_there_are_no_findings()
        {
            // Arrange
            var findings = new AnalyzerFindings();

            // Act
            var report = findings.RenderReport("Code style is correct.", "style");

            // Assert
            Assert.Equal("✅ Code style is correct.", report);
        }

        [Fact]
        public void Should_number_violations_and_prefix_their_lines()
        {
            // Arrange
            var findings = new AnalyzerFindings();
            findings.Add("lang-csharp#INST0005", 3, "#region directives are not allowed.");
            findings.Add("dotnet-coding-standards#INST0017", null, "Keep a single type per file.");

            // Act
            var report = findings.RenderReport("Code style is correct.", "style");

            // Assert
            Assert.Equal(
                "❌ Found 2 style violation(s):\n  1. Line 3: #region directives are not allowed.\n  2. Keep a single type per file.",
                report);
        }

        [Fact]
        public void Should_list_suggestions_without_failing_the_check()
        {
            // Arrange
            var findings = new AnalyzerFindings();
            findings.Suggest("lang-csharp#INST0015", 7, "Missing blank line before 'if' statement.");

            // Act
            var report = findings.RenderReport("Code style is correct.", "style");

            // Assert
            Assert.Multiple(
                () => Assert.True(findings.Passed),
                () => Assert.Equal(
                    "✅ Code style is correct.\n💡 1 optional suggestion(s) — these do not fail the check:\n  1. Line 7: Missing blank line before 'if' statement.",
                    report));
        }

        [Fact]
        public void Should_list_suggestions_after_violations()
        {
            // Arrange
            var findings = new AnalyzerFindings();
            findings.Suggest("lang-csharp#INST0015", 7, "Missing blank line.");
            findings.Add("lang-csharp#INST0005", 3, "No regions.");

            // Act
            var report = findings.RenderReport("Code style is correct.", "style");

            // Assert
            Assert.Multiple(
                () => Assert.False(findings.Passed),
                () => Assert.StartsWith("❌ Found 1 style violation(s):\n  1. Line 3: No regions.\n💡 1 optional", report, StringComparison.Ordinal));
        }
    }

    public sealed class ToOutput
    {
        [Fact]
        public void Should_carry_passed_report_and_structured_findings()
        {
            // Arrange
            var findings = new AnalyzerFindings();
            findings.Add("lang-csharp#INST0005", 3, "No regions.");
            findings.Suggest("lang-csharp#INST0015", null, "Blank line.");

            // Act
            var output = findings.ToOutput("Code style is correct.", "style");

            // Assert
            var items = output.GetProperty("findings").EnumerateArray().ToList();
            Assert.Multiple(
                () => Assert.False(output.GetProperty("passed").GetBoolean()),
                () => Assert.StartsWith("❌", output.GetProperty("report").GetString(), StringComparison.Ordinal),
                () => Assert.Equal(2, items.Count),
                () => Assert.Equal("lang-csharp#INST0005", items[0].GetProperty("ruleId").GetString()),
                () => Assert.Equal("violation", items[0].GetProperty("severity").GetString()),
                () => Assert.Equal(3, items[0].GetProperty("line").GetInt32()),
                () => Assert.Equal("No regions.", items[0].GetProperty("message").GetString()),
                () => Assert.Equal("suggestion", items[1].GetProperty("severity").GetString()),
                () => Assert.False(items[1].TryGetProperty("line", out _)));
        }

        [Fact]
        public void Should_leave_out_findings_the_request_disables()
        {
            // Arrange — one rule disabled by id, another file disabled whole.
            var findings = new AnalyzerFindings();
            findings.Add("lang-csharp#INST0005", 3, "No regions.");
            findings.Add("lang-csharp#INST0021", 5, "Missing docs.");
            findings.Add("dotnet-xunit#INST0009", 9, "No ConfigureAwait in tests.");
            var request = JsonDocument.Parse("""{"content":"x","disabledRules":["lang-csharp#INST0005","dotnet-xunit"]}""").RootElement;

            // Act
            var output = findings.ToOutput("Code style is correct.", "style", request);

            // Assert
            var ruleIds = output.GetProperty("findings").EnumerateArray().Select(static f => f.GetProperty("ruleId").GetString()).ToList();
            Assert.Multiple(
                () => Assert.Equal(["lang-csharp#INST0021"], ruleIds),
                () => Assert.Contains("Found 1 style violation(s)", output.GetProperty("report").GetString(), StringComparison.Ordinal));
        }

        [Fact]
        public void Should_pass_when_every_violation_is_disabled()
        {
            // Arrange
            var findings = new AnalyzerFindings();
            findings.Add("lang-csharp#INST0005", 3, "No regions.");
            var request = JsonDocument.Parse("""{"disabledRules":["lang-csharp#INST0005"]}""").RootElement;

            // Act
            var output = findings.ToOutput("Code style is correct.", "style", request);

            // Assert
            Assert.True(output.GetProperty("passed").GetBoolean());
        }

        [Fact]
        public void Should_emit_an_empty_findings_array_when_clean()
        {
            // Act
            var output = new AnalyzerFindings().ToOutput("Code style is correct.", "style");

            // Assert
            Assert.Multiple(
                () => Assert.True(output.GetProperty("passed").GetBoolean()),
                () => Assert.Equal(JsonValueKind.Array, output.GetProperty("findings").ValueKind),
                () => Assert.Empty(output.GetProperty("findings").EnumerateArray()));
        }
    }

    public sealed class Add
    {
        [Fact]
        public void Should_reject_a_blank_rule_id()
        {
            // Act + Assert
            Assert.Throws<ArgumentException>(() => new AnalyzerFindings().Add(" ", 1, "message"));
        }

        [Fact]
        public void Should_reject_a_blank_message()
        {
            // Act + Assert
            Assert.Throws<ArgumentException>(() => new AnalyzerFindings().Add("lang-csharp#INST0005", 1, ""));
        }
    }
}
