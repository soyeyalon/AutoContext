namespace AutoContext.Worker.DotNet.Tests.Tasks.CSharp;

using AutoContext.Framework.Tests.Support.Workers;
using AutoContext.Worker.DotNet.Tasks.CSharp;
using AutoContext.Worker.DotNet.Tests.Support.Tasks.CSharp;

public sealed class AnalyzeCSharpNullableContextTaskTests
{
    [Fact]
    public async Task Should_pass_code_without_nullable_violations()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public string? Name { get; set; }

                public void SetName(string? name)
                {
                    Name = name;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_code_with_nullable_enable_directive()
    {
        // Arrange
        var source = """
            #nullable enable

            public class MyClass
            {
                public string? Name { get; set; }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_nullable_disable_directive()
    {
        // Arrange
        var source = """
            #nullable disable

            public class MyClass
            {
                public string Name { get; set; }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("#nullable disable", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_null_forgiving_operator()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private string? _name;

                public string GetName()
                {
                    return _name!;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("null-forgiving operator '!'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_null_forgiving_operator_in_expression()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public int GetLength(string? text)
                {
                    return text!.Length;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("null-forgiving operator '!'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_report_multiple_violations()
    {
        // Arrange
        var source = """
            #nullable disable

            public class MyClass
            {
                private string? _name;

                public string GetName()
                {
                    return _name!;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("2 nullable context violation(s)", result, StringComparison.Ordinal);
            Assert.Contains("#nullable disable", result, StringComparison.Ordinal);
            Assert.Contains("null-forgiving operator '!'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_report_line_number_for_nullable_disable()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                #nullable disable
                public string Name { get; set; }
                #nullable restore
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Line 3", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_report_line_number_for_null_forgiving_operator()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private string? _name;

                public string GetName()
                {
                    return _name!;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Line 7", result, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Should_throw_on_empty_or_whitespace_input(string input)
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalyzeCSharpNullableContextTask().ExecuteAsync(new { content = input }));
    }

    [Fact]
    public async Task Should_throw_on_null_input()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalyzeCSharpNullableContextTask().ExecuteAsync(new { content = (string?)null }));
    }

    [Fact]
    public async Task Should_allow_the_null_forgiving_operator_in_a_test_class()
    {
        // Arrange — after an assertion, `!` states what the test has just proved.
        var source = """
            public sealed class ParserTests
            {
                [Fact]
                public void Should_parse()
                {
                    var result = Parser.TryParse("x");
                    Assert.NotNull(result);
                    Assert.Equal("x", result!.Value);
                }
            }
            """;

        // Act
        var (passed, _) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source });

        // Assert
        Assert.True(passed);
    }

    [Fact]
    public async Task Should_allow_the_null_forgiving_operator_anywhere_in_a_test_project()
    {
        // Arrange — test-support helpers carry no [Fact] but are still test code.
        using var project = CSharpProjectTestDirectory.Create(CSharpProjectTestDirectory.Test);
        var source = """
            internal static class Fixtures
            {
                public static string Read(string? value) => value!;
            }
            """;

        // Act
        var (passed, _) = await new AnalyzeCSharpNullableContextTask().GetReportAsync(new { content = source, filePath = project.SourcePath() });

        // Assert
        Assert.True(passed);
    }
}
