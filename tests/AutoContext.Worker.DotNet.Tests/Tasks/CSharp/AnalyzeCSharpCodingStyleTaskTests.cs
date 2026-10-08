namespace AutoContext.Worker.DotNet.Tests.Tasks.CSharp;

using AutoContext.Framework.Tests.Support.Workers;
using AutoContext.Worker.DotNet.Tasks.CSharp;
using AutoContext.Worker.DotNet.Tests.Support.Tasks.CSharp;

public sealed class AnalyzeCSharpCodingStyleTaskTests
{
    [Fact]
    public async Task Should_pass_well_formatted_code()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A sample class.
            /// </summary>
            public class MyClass
            {
                private int _value;

                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork()
                {
                    var x = 1;

                    if (x > 0)
                    {
                        _value = x;
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_region_directives()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                #region Fields
                private int _value;
                #endregion
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("#region", result, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("// ── Lifecycle ──────")]
    [InlineData("// ═══════════════════")]
    [InlineData("// -------------------")]
    [InlineData("// ━━━━━━━━━━━━━━━━━━━")]
    [InlineData("// ___________________")]
    public async Task Should_reject_decorative_comments(string comment)
    {
        // Arrange
        var source = $$"""
            public class MyClass
            {
                {{comment}}
                public void DoWork() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Decorative", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_not_flag_normal_comments()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                // This is a normal comment
                public void DoWork() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("Decorative", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_if_without_curly_braces()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    int x = 1;

                    if (x > 0)
                        x = 2;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("curly braces", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_allow_guard_clause_without_braces()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork(int x)
                {
                    if (x < 0)
                        return;

                    var y = x * 2;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("curly braces", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_allow_guard_clause_with_throw()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork(object obj)
                {
                    if (obj is null)
                        throw new ArgumentNullException(nameof(obj));

                    obj.ToString();
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("curly braces", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_guard_clause_with_else()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork(int x)
                {
                    if (x < 0)
                        return;
                    else
                        x = 2;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("curly braces", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_foreach_without_braces()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    var items = new[] { 1, 2, 3 };

                    foreach (var item in items)
                        Console.WriteLine(item);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("curly braces", result, StringComparison.Ordinal);
            Assert.Contains("foreach", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_while_without_braces()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    int x = 0;

                    while (x < 10)
                        x++;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("while", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_missing_blank_line_before_if()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    var x = 1;
                    if (x > 0)
                    {
                        x = 2;
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("blank line before", result, StringComparison.Ordinal);
            Assert.Contains("if", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_if_at_start_of_block()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    if (true)
                    {
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("blank line before", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_expression_body_arrow_on_same_line()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public int GetValue() => 42;
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Expression-body arrow", result, StringComparison.Ordinal);
            Assert.Contains("next line", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_expression_body_arrow_on_next_line()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public int GetValue()
                    => 42;
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("Expression-body arrow", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_property_expression_body_on_same_line()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private int _value;

                public int Value => _value;
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Expression-body arrow", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_property_expression_body_on_next_line()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private int _value;

                public int Value
                    => _value;
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("Expression-body arrow", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_not_flag_lambda_expression_body()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    var fn = (int x) => x * 2;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("Expression-body arrow", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_report_multiple_violations()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                #region Fields
                private int _value;
                #endregion

                public int GetValue() => _value;
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("#region", result, StringComparison.Ordinal);
            Assert.Contains("Expression-body arrow", result, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Should_throw_on_empty_or_whitespace_input(string input)
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalyzeCSharpCodingStyleTask().ExecuteAsync(new { content = input }));
    }

    [Fact]
    public async Task Should_throw_on_null_input()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalyzeCSharpCodingStyleTask().ExecuteAsync(new { content = (string?)null }));
    }

    [Fact]
    public async Task Should_reject_for_without_braces()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                        Console.WriteLine(i);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("curly braces", result, StringComparison.Ordinal);
            Assert.Contains("for", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_missing_blank_line_before_foreach()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    var items = new[] { 1, 2 };
                    foreach (var item in items)
                    {
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("blank line before", result, StringComparison.Ordinal);
            Assert.Contains("foreach", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_using_without_braces()
    {
        // Arrange
        var source = """
            using System;
            using System.IO;

            public class MyClass
            {
                public void DoWork()
                {
                    using (var stream = new MemoryStream())
                        stream.WriteByte(0);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("curly braces", result, StringComparison.Ordinal);
            Assert.Contains("using", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_using_with_braces()
    {
        // Arrange
        var source = """
            using System;
            using System.IO;

            public class MyClass
            {
                public void DoWork()
                {
                    using (var stream = new MemoryStream())
                    {
                        stream.WriteByte(0);
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("curly braces", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_lock_without_braces()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private readonly object _sync = new();
                private int _count;

                public void DoWork()
                {
                    lock (_sync)
                        _count++;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("curly braces", result, StringComparison.Ordinal);
            Assert.Contains("lock", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_lock_with_braces()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private readonly object _sync = new();
                private int _count;

                public void DoWork()
                {
                    lock (_sync)
                    {
                        _count++;
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("curly braces", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_fixed_without_braces()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public unsafe void DoWork()
                {
                    var arr = new int[] { 1, 2, 3 };

                    fixed (int* p = arr)
                        *p = 42;
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("curly braces", result, StringComparison.Ordinal);
            Assert.Contains("fixed", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_missing_blank_line_before_using()
    {
        // Arrange
        var source = """
            using System;
            using System.IO;

            public class MyClass
            {
                public void DoWork()
                {
                    var x = 1;
                    using (var stream = new MemoryStream())
                    {
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("blank line before", result, StringComparison.Ordinal);
            Assert.Contains("using", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_missing_blank_line_before_lock()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private readonly object _sync = new();

                public void DoWork()
                {
                    var x = 1;
                    lock (_sync)
                    {
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("blank line before", result, StringComparison.Ordinal);
            Assert.Contains("lock", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_missing_blank_line_before_try()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    var x = 1;
                    try
                    {
                        x = 2;
                    }
                    catch
                    {
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("blank line before", result, StringComparison.Ordinal);
            Assert.Contains("try", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_try_with_blank_line_before()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public void DoWork()
                {
                    var x = 1;

                    try
                    {
                        x = 2;
                    }
                    catch
                    {
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("blank line before", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_public_class_without_xml_doc()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private void DoWork() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("XML doc", result, StringComparison.Ordinal);
            Assert.Contains("MyClass", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_public_method_without_xml_doc()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                public void DoWork() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("XML doc", result, StringComparison.Ordinal);
            Assert.Contains("DoWork", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_protected_method_without_xml_doc()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                protected void Helper() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("XML doc", result, StringComparison.Ordinal);
            Assert.Contains("Helper", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_private_method_without_xml_doc()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                private void Helper() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("XML doc", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_internal_method_without_xml_doc()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                internal void Helper() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("XML doc", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_override_method_without_xml_doc()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                public override string ToString() => "x";
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("XML doc", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_skip_xml_doc_check_for_test_class()
    {
        // Arrange
        var source = """
            public class MyTests
            {
                public void DoWork() { }

                [Fact]
                public async Task Should_work() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("XML doc", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_public_property_with_xml_doc()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Gets or sets the name.
                /// </summary>
                public string Name { get; set; }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("XML doc", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_public_property_without_xml_doc()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                public string Name { get; set; }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("XML doc", result, StringComparison.Ordinal);
            Assert.Contains("Name", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_public_constructor_without_xml_doc()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                public MyClass() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("XML doc", result, StringComparison.Ordinal);
            Assert.Contains("MyClass", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_do_without_curly_braces()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork()
                {
                    var x = 0;

                    do
                        x++;
                    while (x < 10);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("curly braces", result, StringComparison.Ordinal);
            Assert.Contains("'do'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_suggest_a_blank_line_before_switch()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork(int x)
                {
                    var y = 0;
                    switch (x)
                    {
                        case 1:
                            y = 1;
                            break;
                    }
                }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.True(passed);
            Assert.Contains("optional suggestion", result, StringComparison.Ordinal);
            Assert.Contains("blank line", result, StringComparison.Ordinal);
            Assert.Contains("'switch'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_not_flag_else_if_for_blank_line()
    {
        // Arrange
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork(int x)
                {
                    var y = 0;

                    if (x == 1)
                    {
                        y = 1;
                    }
                    else if (x == 2)
                    {
                        y = 2;
                    }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("blank line", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_public_enum_without_xml_doc()
    {
        // Arrange
        var source = """
            public enum Status
            {
                Active,
                Inactive,
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("XML doc", result, StringComparison.Ordinal);
            Assert.Contains("Status", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_public_delegate_without_xml_doc()
    {
        // Arrange
        var source = """
            public delegate void MyHandler(object sender);
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("XML doc", result, StringComparison.Ordinal);
            Assert.Contains("MyHandler", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_flag_unnecessary_braces_when_editorconfig_false()
    {
        // Arrange — single-line body with braces
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork()
                {
                    var items = new[] { 1, 2, 3 };

                    foreach (var item in items)
                    {
                        Console.WriteLine(item);
                    }
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_prefer_braces"] = "false" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("unnecessary curly braces", result, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Should_pass_braceless_body_when_editorconfig_false()
    {
        // Arrange — no braces on single-line body
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork()
                {
                    var items = new[] { 1, 2, 3 };

                    foreach (var item in items)
                        Console.WriteLine(item);
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_prefer_braces"] = "false" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("curly braces", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_flag_unnecessary_braces_on_single_line_when_editorconfig_when_multiline()
    {
        // Arrange — single-line body with braces
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork()
                {
                    var items = new[] { 1, 2, 3 };

                    foreach (var item in items)
                    {
                        Console.WriteLine(item);
                    }
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_prefer_braces"] = "when_multiline" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("unnecessary curly braces", result, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Should_require_braces_on_multiline_when_editorconfig_when_multiline()
    {
        // Arrange — multi-line body without braces
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork()
                {
                    var items = new[] { 1, 2, 3 };

                    foreach (var item in items)
                        Console.WriteLine(
                            item);
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_prefer_braces"] = "when_multiline" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("requires curly braces", result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("multi-line", result, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Should_enforce_curly_braces_when_editorconfig_says_true()
    {
        // Arrange — missing braces on if
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork()
                {
                    var items = new[] { 1, 2, 3 };

                    foreach (var item in items)
                        Console.WriteLine(item);
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_prefer_braces"] = "true" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert — should still enforce braces
        Assert.Contains("curly braces", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_enforce_curly_braces_when_no_editorconfig()
    {
        // Arrange — missing braces on foreach, no data
        var source = """
            /// <summary>
            /// A class.
            /// </summary>
            public class MyClass
            {
                /// <summary>
                /// Does work.
                /// </summary>
                public void DoWork()
                {
                    var items = new[] { 1, 2, 3 };

                    foreach (var item in items)
                        Console.WriteLine(item);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert — default behavior: enforce braces
        Assert.Contains("curly braces", result, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // dotnet_sort_system_directives_first
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_flag_system_using_after_non_system_using_by_default()
    {
        // Arrange
        var source = """
            using Microsoft.Extensions.Logging;
            using System.Collections.Generic;

            public class MyClass { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("System.Collections.Generic", result, StringComparison.Ordinal);
            Assert.Contains("non-System using directives", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_system_usings_before_non_system_usings()
    {
        // Arrange — System.* usings come first
        var source = """
            using System.Collections.Generic;
            using Microsoft.Extensions.Logging;

            public class MyClass { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("non-System using directives", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_only_system_usings()
    {
        // Arrange
        var source = """
            using System;
            using System.Collections.Generic;
            using System.IO;

            public class MyClass { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("non-System using directives", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_only_non_system_usings()
    {
        // Arrange
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Logging;

            public class MyClass { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("non-System using directives", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_skip_sort_check_when_dotnet_sort_system_directives_first_false()
    {
        // Arrange — System.* after non-System.*, but setting is false
        var source = """
            using Microsoft.Extensions.Logging;
            using System.Collections.Generic;

            public class MyClass { }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.dotnet_sort_system_directives_first"] = "false" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("non-System using directives", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_flag_system_using_after_non_system_when_setting_true()
    {
        // Arrange — explicit true is the same as the default
        var source = """
            using Newtonsoft.Json;
            using System.Text;

            public class MyClass { }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.dotnet_sort_system_directives_first"] = "true" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("System.Text", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_skip_static_and_alias_usings_in_sort_check()
    {
        // Arrange — static and alias usings are ignored; only plain ordering matters
        var source = """
            using static System.Math;
            using Alias = System.Collections.Generic.List<int>;
            using Microsoft.Extensions.Logging;
            using System.Collections.Generic;

            public class MyClass { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("System.Collections.Generic", result, StringComparison.Ordinal);
            Assert.Contains("non-System using directives", result, StringComparison.Ordinal);
        });
    }

    // -------------------------------------------------------------------------
    // csharp_style_expression_bodied_methods
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_flag_expression_body_method_when_never()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public int GetValue()
                    => 42;
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_methods"] = "never" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("GetValue", result, StringComparison.Ordinal);
            Assert.Contains("expression body", result, StringComparison.Ordinal);
            Assert.Contains("never", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_block_body_method_when_never()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public int GetValue()
                {
                    return 42;
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_methods"] = "never" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("expression body", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_flag_block_body_single_return_method_when_always()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                public int GetValue()
                {
                    return 42;
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_methods"] = "always" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("GetValue", result, StringComparison.Ordinal);
            Assert.Contains("single return statement", result, StringComparison.Ordinal);
            Assert.Contains("always", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_expression_body_method_when_always()
    {
        // Arrange — already expression-bodied, should not be flagged by this check
        var source = """
            public class MyClass
            {
                public int GetValue()
                    => 42;
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_methods"] = "always" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("single return statement", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_multi_statement_method_when_always()
    {
        // Arrange — two statements: cannot be expression-bodied
        var source = """
            public class MyClass
            {
                public int GetValue()
                {
                    var x = 1;
                    return x * 2;
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_methods"] = "always" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("single return statement", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_flag_single_line_return_method_when_when_on_single_line()
    {
        // Arrange — return expression is a single-line literal
        var source = """
            public class MyClass
            {
                public int GetValue()
                {
                    return 42;
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_methods"] = "when_on_single_line" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("GetValue", result, StringComparison.Ordinal);
            Assert.Contains("when_on_single_line", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_multi_line_return_expression_when_when_on_single_line()
    {
        // Arrange — return expression spans multiple lines
        var source = """
            public class MyClass
            {
                public string Build()
                {
                    return string.Join(
                        ", ",
                        new[] { "a", "b", "c" });
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_methods"] = "when_on_single_line" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("when_on_single_line", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_not_check_expression_body_methods_when_no_editorconfig_key()
    {
        // Arrange — expression body without any preference set
        var source = """
            public class MyClass
            {
                public int GetValue()
                    => 42;
            }
            """;

        // Act — no data, so preference is null → no check
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("csharp_style_expression_bodied_methods", result, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // csharp_style_expression_bodied_properties
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_flag_expression_body_property_when_never()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private int _value;

                public int Value
                    => _value;
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_properties"] = "never" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Value", result, StringComparison.Ordinal);
            Assert.Contains("expression body", result, StringComparison.Ordinal);
            Assert.Contains("never", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_block_body_property_when_never()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private int _value;

                public int Value
                {
                    get { return _value; }
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_properties"] = "never" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("expression body", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_flag_get_only_single_return_property_when_always()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private int _value;

                public int Value
                {
                    get { return _value; }
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_properties"] = "always" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Value", result, StringComparison.Ordinal);
            Assert.Contains("single return", result, StringComparison.Ordinal);
            Assert.Contains("always", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_auto_property_when_always()
    {
        // Arrange — auto-property has no body to convert
        var source = """
            public class MyClass
            {
                public int Value { get; set; }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_properties"] = "always" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("single return", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_get_set_property_when_always()
    {
        // Arrange — two accessors: cannot use property-level expression body
        var source = """
            public class MyClass
            {
                private int _value;

                public int Value
                {
                    get { return _value; }
                    set { _value = value; }
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_properties"] = "always" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("single return", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_flag_single_line_get_return_when_when_on_single_line()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private int _value;

                public int Value
                {
                    get { return _value; }
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_properties"] = "when_on_single_line" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Value", result, StringComparison.Ordinal);
            Assert.Contains("when_on_single_line", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_multi_line_get_return_when_when_on_single_line()
    {
        // Arrange — return expression spans multiple lines
        var source = """
            public class MyClass
            {
                private readonly string[] _items = [];

                public string Value
                {
                    get
                    {
                        return string.Join(
                            ", ",
                            _items);
                    }
                }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_expression_bodied_properties"] = "when_on_single_line" };

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("when_on_single_line", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_not_check_expression_body_properties_when_no_editorconfig_key()
    {
        // Arrange
        var source = """
            public class MyClass
            {
                private int _value;

                public int Value
                    => _value;
            }
            """;

        // Act — no data, so preference is null → no check
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("csharp_style_expression_bodied_properties", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_skip_global_usings_in_sort_check()
    {
        // Arrange — global using for a non-System namespace followed by a System using
        var source = """
            global using ThirdParty;
            using System;

            public class MyClass { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("non-System using directives", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_not_require_xml_docs_on_members_of_an_internal_type()
    {
        // Arrange — a public member of an internal type is not visible outside the assembly.
        var source = """
            namespace MyApp;

            internal sealed class Helper
            {
                public void Run() { }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(
            () => Assert.True(passed),
            () => Assert.DoesNotContain("XML doc", result, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_only_suggest_xml_docs_in_an_application()
    {
        // Arrange — an application publishes no API for others to read.
        using var project = CSharpProjectTestDirectory.Create(CSharpProjectTestDirectory.Web);
        var source = """
            namespace MyApp;

            /// <summary>Entry.</summary>
            public sealed class Worker
            {
                public void Run() { }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source, filePath = project.SourcePath() });

        // Assert
        Assert.Multiple(
            () => Assert.True(passed),
            () => Assert.Contains("optional suggestion", result, StringComparison.Ordinal),
            () => Assert.Contains("XML doc", result, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_require_xml_docs_on_public_api_of_a_library()
    {
        // Arrange
        using var project = CSharpProjectTestDirectory.Create(CSharpProjectTestDirectory.Library);
        var source = """
            namespace MyLib;

            /// <summary>Public API.</summary>
            public sealed class Client
            {
                public void Send() { }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source, filePath = project.SourcePath() });

        // Assert
        Assert.Multiple(
            () => Assert.False(passed),
            () => Assert.Contains("'Send' is missing XML doc", result, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_not_require_braces_around_an_else_if_chain()
    {
        // Arrange
        var source = """
            namespace MyApp;

            internal static class Classifier
            {
                public static string Classify(int value)
                {
                    if (value < 0)
                    {
                        return "negative";
                    }
                    else if (value == 0)
                    {
                        return "zero";
                    }

                    return "positive";
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("'else' statement requires curly braces", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_not_require_xml_docs_on_a_class_that_groups_nested_tests()
    {
        // Arrange
        var source = """
            namespace MyApp.Tests;

            public sealed class ParserTests
            {
                public sealed class Parse
                {
                    [Fact]
                    public void Should_parse() { }
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpCodingStyleTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("XML doc", result, StringComparison.Ordinal);
    }
}
