namespace AutoContext.Worker.DotNet.Tests.Tasks.CSharp;

using AutoContext.Framework.Tests.Support.Workers;
using AutoContext.Worker.DotNet.Tasks.CSharp;
using AutoContext.Workers.Core;

/// <summary>
/// The acceptance bar for the C# checks: Microsoft's own <c>dotnet new</c>
/// templates are idiomatic .NET, so a check that fails one is judging taste
/// or context rather than correctness — and an agent told to fix it would
/// rewrite correct code. Every gating finding on a template must come from a
/// convention this corpus deliberately adds on top of .NET's defaults; each
/// such rule is listed below with its reason, and anything else fails.
/// </summary>
public sealed class StockTemplatesTests
{
    private static readonly IMcpTask[] Checks =
    [
        new AnalyzeCSharpCodingStyleTask(),
        new AnalyzeCSharpMemberOrderingTask(),
        new AnalyzeCSharpNamingConventionsTask(),
        new AnalyzeCSharpAsyncPatternsTask(),
        new AnalyzeCSharpNullableContextTask(),
        new AnalyzeCSharpProjectStructureTask(),
        new AnalyzeCSharpTestStyleTask(),
    ];

    /// <summary>
    /// Conventions the corpus adds that the templates do not follow, by
    /// template file. Nothing else may gate.
    /// </summary>
    private static readonly Dictionary<string, string[]> DeliberateConventions = new(StringComparer.Ordinal)
    {
        // A library's public API carries XML docs (lang-csharp INST0021); the template's Class1 has none.
        ["classlib/Class1.cs"] = ["lang-csharp#INST0021"],

        // Test classes and files are named <UnitUnderTest>Tests (dotnet-testing INST0005, INST0007);
        // the template's placeholder is UnitTest1.
        ["xunit/UnitTest1.cs"] = ["dotnet-testing#INST0005", "dotnet-testing#INST0007"],

        // One type per file (dotnet-coding-standards INST0017); the template keeps its
        // WeatherForecast record beside the top-level statements for brevity.
        ["webapi/Program.cs"] = ["dotnet-coding-standards#INST0017"],
    };

    public static TheoryData<string> TemplateFiles() =>
    [
        "classlib/Class1.cs",
        "console/Program.cs",
        "webapi/Program.cs",
        "worker/Program.cs",
        "worker/Worker.cs",
        "xunit/UnitTest1.cs",
    ];

    [Theory]
    [MemberData(nameof(TemplateFiles))]
    public async Task Should_gate_stock_templates_only_on_deliberate_conventions(string templateFile)
    {
        // Arrange
        var filePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StockTemplates", templateFile);
        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        var allowed = DeliberateConventions.GetValueOrDefault(templateFile, []);

        // Act
        var gating = new List<string>();

        foreach (var check in Checks)
        {
            var output = await check.ExecuteAsync(new { content, filePath });

            gating.AddRange(output.GetProperty("findings")
                .EnumerateArray()
                .Where(static finding => finding.GetProperty("severity").GetString() == "violation")
                .Select(static finding => $"{finding.GetProperty("ruleId").GetString()}: {finding.GetProperty("message").GetString()}"));
        }

        var unexpected = gating.Where(finding => !allowed.Any(rule => finding.StartsWith(rule + ":", StringComparison.Ordinal))).ToList();

        // Assert
        Assert.Empty(unexpected);
    }

    [Fact]
    public void Should_list_only_templates_that_exist()
    {
        // Arrange
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StockTemplates");

        // Act + Assert
        Assert.All(DeliberateConventions.Keys, file => Assert.True(File.Exists(Path.Combine(root, file)), file));
    }

    [Fact]
    public async Task Should_still_report_the_deliberate_conventions()
    {
        // Arrange — the allowances above must stay true, or they hide nothing and should go.
        var filePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StockTemplates", "classlib", "Class1.cs");
        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);

        // Act
        var output = await new AnalyzeCSharpCodingStyleTask().ExecuteAsync(new { content, filePath });

        // Assert
        Assert.Contains(
            output.GetProperty("findings").EnumerateArray(),
            static finding => finding.GetProperty("ruleId").GetString() == "lang-csharp#INST0021");
    }
}
