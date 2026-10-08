namespace AutoContext.Instructions.Manifest.Generator.Tests;

using AutoContext.Engine.Tests.Support.IO;
using AutoContext.Instructions.Manifest.Generator;
using AutoContext.Instructions.Manifest.Generator.Tests.Support;

public sealed class McpToolsRegistryReaderTests
{
    public sealed class Read(TempDirectoryFixture tempDirectory) : IClassFixture<TempDirectoryFixture>
    {
        private readonly McpToolsRegistryReader _sut = new();

        [Fact]
        public void Should_reject_null_registry_path()
        {
            // Act + Assert
            Assert.Throws<ArgumentNullException>(() => _sut.Read(null!));
        }

        [Fact]
        public void Should_read_tool_names_and_parameter_names()
        {
            // Arrange
            var registryPath = McpToolsRegistryTestWriter.Write(
                tempDirectory.CreateDirectory(),
                ("analyze_csharp_code_style", ["content", "filePath"]),
                ("analyze_git_commit_message", ["content"]));

            // Act
            var registry = _sut.Read(registryPath);

            // Assert
            Assert.Multiple(
                () => Assert.Equal(2, registry.Count),
                () => Assert.Equal(["content", "filePath"], registry["analyze_csharp_code_style"].ParameterNames.Order()),
                () => Assert.Equal(["content"], registry["analyze_git_commit_message"].ParameterNames.Order()));
        }

        [Fact]
        public void Should_yield_no_parameters_for_a_tool_that_declares_none()
        {
            // Arrange
            var registryPath = McpToolsRegistryTestWriter.WriteRaw(
                tempDirectory.CreateDirectory(),
                """{ "tools": [ { "name": "list_things" } ] }""");

            // Act
            var registry = _sut.Read(registryPath);

            // Assert
            Assert.Empty(Assert.Single(registry).Value.ParameterNames);
        }

        [Fact]
        public void Should_fail_when_the_registry_file_is_missing()
        {
            // Arrange
            var registryPath = Path.Combine(tempDirectory.CreateDirectory(), "mcp-tools-registry.json");

            // Act
            var exception = Assert.Throws<InvalidOperationException>(() => _sut.Read(registryPath));

            // Assert
            Assert.StartsWith("[mcp-tools-registry.json] registry file not found", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Should_fail_when_the_registry_is_not_json()
        {
            // Arrange
            var registryPath = McpToolsRegistryTestWriter.WriteRaw(tempDirectory.CreateDirectory(), "{ not json");

            // Act
            var exception = Assert.Throws<InvalidOperationException>(() => _sut.Read(registryPath));

            // Assert
            Assert.StartsWith("[mcp-tools-registry.json] registry is not valid JSON", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Should_fail_when_the_tools_array_is_missing()
        {
            // Arrange
            var registryPath = McpToolsRegistryTestWriter.WriteRaw(tempDirectory.CreateDirectory(), """{ "schemaVersion": "1" }""");

            // Act
            var exception = Assert.Throws<InvalidOperationException>(() => _sut.Read(registryPath));

            // Assert
            Assert.Equal("[mcp-tools-registry.json] registry is missing its `tools` array", exception.Message);
        }

        [Fact]
        public void Should_fail_when_a_tool_has_no_name()
        {
            // Arrange
            var registryPath = McpToolsRegistryTestWriter.WriteRaw(
                tempDirectory.CreateDirectory(),
                """{ "tools": [ { "workerId": "dotnet" } ] }""");

            // Act
            var exception = Assert.Throws<InvalidOperationException>(() => _sut.Read(registryPath));

            // Assert
            Assert.Equal("[mcp-tools-registry.json] a tool has a missing or blank `name`", exception.Message);
        }

        [Fact]
        public void Should_fail_on_a_duplicate_tool_name()
        {
            // Arrange
            var registryPath = McpToolsRegistryTestWriter.Write(
                tempDirectory.CreateDirectory(),
                ("analyze_csharp_code_style", ["content"]),
                ("analyze_csharp_code_style", ["content"]));

            // Act
            var exception = Assert.Throws<InvalidOperationException>(() => _sut.Read(registryPath));

            // Assert
            Assert.Equal("[mcp-tools-registry.json] duplicate tool 'analyze_csharp_code_style'", exception.Message);
        }
    }
}
