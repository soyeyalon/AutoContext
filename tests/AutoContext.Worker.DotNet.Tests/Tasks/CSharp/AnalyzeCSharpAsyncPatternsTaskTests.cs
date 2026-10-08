namespace AutoContext.Worker.DotNet.Tests.Tasks.CSharp;

using AutoContext.Framework.Tests.Support.Workers;
using AutoContext.Worker.DotNet.Tasks.CSharp;

public sealed class AnalyzeCSharpAsyncPatternsTaskTests
{
    [Fact]
    public async Task Should_pass_correct_async_code()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async Task LoadAsync(CancellationToken cancellationToken = default)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.True(passed);
            Assert.StartsWith("✅", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_report_a_structured_finding_with_its_rule_and_line()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async void LoadData() { }
            }
            """;

        // Act
        var output = await new AnalyzeCSharpAsyncPatternsTask().ExecuteAsync(new { content = source });

        // Assert
        var finding = Assert.Single(output.GetProperty("findings").EnumerateArray());
        Assert.Multiple(
            () => Assert.Equal("dotnet-async-await#INST0007", finding.GetProperty("ruleId").GetString()),
            () => Assert.Equal("violation", finding.GetProperty("severity").GetString()),
            () => Assert.Equal(3, finding.GetProperty("line").GetInt32()),
            () => Assert.DoesNotContain("Line ", finding.GetProperty("message").GetString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_reject_async_void()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async void LoadData() { }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.False(passed);
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("async void", result, StringComparison.Ordinal);
            Assert.Contains("LoadData", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_event_handler_async_void()
    {
        // Arrange
        var source = """
            public class MyForm
            {
                public async void OnButtonClicked(object sender, EventArgs e)
                {
                    await Task.Delay(0).ConfigureAwait(false);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("async void", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_public_async_without_cancellation_token()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async Task LoadAsync() { }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.False(passed);
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("CancellationToken", result, StringComparison.Ordinal);
            Assert.Contains("LoadAsync", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_public_async_with_cancellation_token()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async Task LoadAsync(CancellationToken cancellationToken = default) { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("CancellationToken", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_public_async_with_fully_qualified_cancellation_token()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async Task LoadAsync(System.Threading.CancellationToken cancellationToken = default) { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("CancellationToken", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_skip_override_for_cancellation_token_check()
    {
        // Arrange
        var source = """
            public abstract class Base
            {
                public abstract Task LoadAsync();
            }

            public class Derived : Base
            {
                public override async Task LoadAsync() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("CancellationToken", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_skip_async_void_for_cancellation_token_check()
    {
        // Arrange
        var source = """
            public class MyForm
            {
                public async void OnButtonClicked(object sender, EventArgs e)
                {
                    await Task.Delay(0).ConfigureAwait(false);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("CancellationToken", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_skip_private_async_for_cancellation_token_check()
    {
        // Arrange
        var source = """
            public class MyService
            {
                private async Task InternalLoadAsync() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("CancellationToken", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_await_without_configure_await()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async Task LoadAsync(CancellationToken cancellationToken = default)
                {
                    await Task.Delay(100);
                }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.False(passed);
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("ConfigureAwait(false)", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_await_with_configure_await_false()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async Task LoadAsync(CancellationToken cancellationToken = default)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("ConfigureAwait", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_await_with_configure_await_true()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async Task LoadAsync(CancellationToken cancellationToken = default)
                {
                    await Task.Delay(100).ConfigureAwait(true);
                }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.False(passed);
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("ConfigureAwait(false)", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_skip_configure_await_check_in_test_class()
    {
        // Arrange
        var source = """
            public class MyServiceTests
            {
                [Fact]
                public async Task Should_load_data()
                {
                    await Task.Delay(0);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("ConfigureAwait", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_flag_multiple_configure_await_violations()
    {
        // Arrange
        var source = """
            public class MyService
            {
                public async Task LoadAsync(CancellationToken cancellationToken = default)
                {
                    await Task.Delay(100);
                    await Task.Delay(200);
                }
            }
            """;

        // Act
        var (passed, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.False(passed);
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("2 async pattern violation(s)", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_on_named_async_void_as_event_handler()
    {
        // Arrange — OnXxx naming convention covers Blazor lifecycle, WPF overrides, etc.
        var source = """
            public class MyComponent
            {
                protected async void OnInitialized()
                {
                    await Task.Delay(0).ConfigureAwait(false);
                }

                protected async void OnAfterRender(bool firstRender)
                {
                    await Task.Delay(0).ConfigureAwait(false);
                }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpAsyncPatternsTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("async void", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Should_throw_on_empty_or_whitespace_input(string input)
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalyzeCSharpAsyncPatternsTask().ExecuteAsync(new { content = input }));
    }

    [Fact]
    public async Task Should_throw_on_null_input()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalyzeCSharpAsyncPatternsTask().ExecuteAsync(new { content = (string?)null }));
    }
}
