namespace AutoContext.Worker.DotNet.Tests.Tasks.CSharp;

using AutoContext.Framework.Tests.Support.Workers;
using AutoContext.Worker.DotNet.Tasks.CSharp;

public sealed class AnalyzeCSharpProjectStructureTaskTests
{
    [Fact]
    public async Task Should_pass_well_structured_file()
    {
        // Arrange
        var source = """
            namespace MyApp.Services;

            public sealed class UserService
            {
                public void DoWork() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "UserService.cs" });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_without_file_name()
    {
        // Arrange
        var source = """
            namespace MyApp.Services;

            public sealed class UserService
            {
                public void DoWork() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_block_scoped_namespace()
    {
        // Arrange
        var source = """
            namespace MyApp.Services
            {
                public class UserService { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Block-scoped namespace", result, StringComparison.Ordinal);
            Assert.Contains("file-scoped namespace", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_file_scoped_namespace()
    {
        // Arrange
        var source = """
            namespace MyApp.Services;

            public class UserService { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("namespace", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_multiple_types_in_one_file()
    {
        // Arrange
        var source = """
            namespace MyApp.Models;

            public class User { }

            public class UserDto { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("2 top-level type declarations", result, StringComparison.Ordinal);
            Assert.Contains("'User'", result, StringComparison.Ordinal);
            Assert.Contains("'UserDto'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_type_and_delegate_in_same_file()
    {
        // Arrange
        var source = """
            namespace MyApp.Models;

            public delegate void MyCallback(int result);

            public class EventProcessor { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("2 top-level type declarations", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_single_type_with_nested_type()
    {
        // Arrange
        var source = """
            namespace MyApp.Models;

            public class Outer
            {
                private class Inner { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("top-level type", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_check_the_file_name_from_the_engine_file_path_argument()
    {
        // Arrange — the engine passes `filePath`; the legacy server sent `originalPath`.
        var source = """
            namespace MyApp.Services;

            public class UserService { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["filePath"] = "WrongName.cs" });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("'WrongName.cs'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_reject_file_name_mismatch()
    {
        // Arrange
        var source = """
            namespace MyApp.Services;

            public class UserService { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "WrongName.cs" });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("'WrongName.cs'", result, StringComparison.Ordinal);
            Assert.Contains("'UserService'", result, StringComparison.Ordinal);
            Assert.Contains("Rename the file to 'UserService.cs'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_file_name_matching_type()
    {
        // Arrange
        var source = """
            namespace MyApp.Services;

            public class UserService { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "UserService.cs" });

        // Assert
        Assert.DoesNotContain("file name", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_reject_pragma_warning_disable()
    {
        // Arrange
        var source = """
            namespace MyApp.Services;

            #pragma warning disable CA1822
            public class UserService
            {
                public void DoWork() { }
            }
            #pragma warning restore CA1822
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("#pragma warning disable", result, StringComparison.Ordinal);
            Assert.Contains("[SuppressMessage]", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_pragma_warning_restore()
    {
        // Arrange
        var source = """
            namespace MyApp.Services;

            public class UserService
            {
                #pragma warning restore CA1822
                public void DoWork() { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.DoesNotContain("#pragma", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_report_multiple_violations()
    {
        // Arrange
        var source = """
            namespace MyApp.Services
            {
                #pragma warning disable CA1822
                public class UserService { }
                public class AnotherService { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("Block-scoped namespace", result, StringComparison.Ordinal);
            Assert.Contains("top-level type declarations", result, StringComparison.Ordinal);
            Assert.Contains("#pragma warning disable", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_not_check_file_name_when_multiple_types()
    {
        // Arrange
        var source = """
            namespace MyApp.Models;

            public class User { }

            public class UserDto { }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "User.cs" });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.Contains("top-level type declarations", result, StringComparison.Ordinal);
            Assert.DoesNotContain("file name", result, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Should_pass_single_delegate_file()
    {
        // Arrange
        var source = """
            namespace MyApp.Models;

            public delegate void DataCallback(int result);
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "DataCallback.cs" });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_reject_delegate_file_name_mismatch()
    {
        // Arrange
        var source = """
            namespace MyApp.Models;

            public delegate void DataCallback(int result);
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "Wrong.cs" });

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("'DataCallback'", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_interface_file()
    {
        // Arrange
        var source = """
            namespace MyApp.Abstractions;

            public interface IUserRepository
            {
                void Save();
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "IUserRepository.cs" });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_enum_file()
    {
        // Arrange
        var source = """
            namespace MyApp.Models;

            public enum Status
            {
                Active,
                Inactive,
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "Status.cs" });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_pass_record_file()
    {
        // Arrange
        var source = """
            namespace MyApp.Models;

            public record UserDto(string Name, int Age);
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new Dictionary<string, object> { ["content"] = source, ["originalPath"] = "UserDto.cs" });

        // Assert
        Assert.StartsWith("✅", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Should_throw_on_empty_or_whitespace_input(string input)
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalyzeCSharpProjectStructureTask().ExecuteAsync(new { content = input }));
    }

    [Fact]
    public async Task Should_throw_on_null_input()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalyzeCSharpProjectStructureTask().ExecuteAsync(new { content = (string?)null }));
    }

    [Fact]
    public async Task Should_enforce_block_scoped_namespace_when_editorconfig_says_block_scoped()
    {
        // Arrange — file-scoped namespace, should be flagged when block_scoped is preferred
        var source = """
            namespace MyApp.Services;

            public class UserService { }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_namespace_declarations"] = "block_scoped" };

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(data);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.StartsWith("❌", result, StringComparison.Ordinal);
            Assert.Contains("File-scoped namespace", result, StringComparison.Ordinal);
            Assert.Contains("block_scoped", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Should_pass_block_scoped_namespace_when_editorconfig_says_block_scoped()
    {
        // Arrange — block-scoped namespace, matching the preference
        var source = """
            namespace MyApp.Services
            {
                public class UserService { }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_namespace_declarations"] = "block_scoped" };

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(data);

        // Assert
        Assert.DoesNotContain("namespace", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_enforce_file_scoped_namespace_when_editorconfig_says_file_scoped()
    {
        // Arrange — block-scoped namespace
        var source = """
            namespace MyApp.Services
            {
                public class UserService { }
            }
            """;

        var data = new Dictionary<string, object> { ["content"] = source, ["editorconfig.csharp_style_namespace_declarations"] = "file_scoped" };

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(data);

        // Assert — should still report block-scoped violation
        Assert.Contains("Block-scoped namespace", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_enforce_file_scoped_namespace_when_no_editorconfig()
    {
        // Arrange — block-scoped namespace, no data
        var source = """
            namespace MyApp.Services
            {
                public class UserService { }
            }
            """;

        // Act
        var (_, result) = await new AnalyzeCSharpProjectStructureTask().GetReportAsync(new { content = source });

        // Assert — default: enforce file-scoped
        Assert.Contains("Block-scoped namespace", result, StringComparison.Ordinal);
    }
}
