namespace AutoContext.Engine.Core.Tests.Features.McpTools;

using AutoContext.Engine.Core.Features.McpTools;
using AutoContext.Engine.Core.Tests.Support.Features.McpTools;

public sealed class McpToolsRegistrySchemaValidatorTests
{
    public sealed class Validate
    {
        [Fact]
        public void Should_accept_a_valid_registry()
        {
            // Act
            var result = McpToolsRegistrySchemaValidator.Validate(
                McpToolsRegistryTestFiles.RegistryJson,
                McpToolsRegistryTestFiles.SchemaJson);

            // Assert
            Assert.Multiple(
                () => Assert.True(result.IsValid),
                () => Assert.Empty(result.Errors));
        }

        [Fact]
        public void Should_report_a_schema_violation()
        {
            // Arrange — the tool name violates the snake_case pattern.
            var registry =
                """
                {
                  "schemaVersion": "1",
                  "tools": [
                    {
                      "name": "AnalyzeSampleCode",
                      "workerId": "dotnet",
                      "description": "Analyse sample source.",
                      "parameters": {
                        "content": { "type": "string", "description": "The source text." }
                      }
                    }
                  ]
                }
                """;

            // Act
            var result = McpToolsRegistrySchemaValidator.Validate(
                registry, McpToolsRegistryTestFiles.SchemaJson);

            // Assert
            Assert.Multiple(
                () => Assert.False(result.IsValid),
                () => Assert.NotEmpty(result.Errors));
        }

        [Fact]
        public void Should_report_a_missing_required_field()
        {
            // Arrange — the tool omits its description.
            var registry =
                """
                {
                  "schemaVersion": "1",
                  "tools": [
                    {
                      "name": "analyze_sample_code",
                      "workerId": "dotnet",
                      "parameters": {
                        "content": { "type": "string", "description": "The source text." }
                      }
                    }
                  ]
                }
                """;

            // Act
            var result = McpToolsRegistrySchemaValidator.Validate(
                registry, McpToolsRegistryTestFiles.SchemaJson);

            // Assert
            Assert.False(result.IsValid);
        }

        [Fact]
        public void Should_report_an_empty_task_list()
        {
            // Arrange — a declared task list must name at least one task; omit it instead.
            var registry =
                """
                {
                  "schemaVersion": "1",
                  "tools": [
                    {
                      "name": "analyze_sample_code",
                      "workerId": "dotnet",
                      "tasks": [],
                      "description": "Analyse sample source.",
                      "parameters": {
                        "content": { "type": "string", "description": "The source text." }
                      }
                    }
                  ]
                }
                """;

            // Act
            var result = McpToolsRegistrySchemaValidator.Validate(
                registry, McpToolsRegistryTestFiles.SchemaJson);

            // Assert
            Assert.False(result.IsValid);
        }

        [Fact]
        public void Should_report_a_duplicate_tool_name()
        {
            // Arrange — two schema-valid tools share a name.
            var registry =
                """
                {
                  "schemaVersion": "1",
                  "tools": [
                    {
                      "name": "analyze_sample_code",
                      "workerId": "dotnet",
                      "description": "First.",
                      "parameters": {
                        "content": { "type": "string", "description": "The source text." }
                      }
                    },
                    {
                      "name": "analyze_sample_code",
                      "workerId": "workspace",
                      "description": "Second.",
                      "parameters": {
                        "filePath": { "type": "string", "description": "Absolute path." }
                      }
                    }
                  ]
                }
                """;

            // Act
            var result = McpToolsRegistrySchemaValidator.Validate(
                registry, McpToolsRegistryTestFiles.SchemaJson);

            // Assert
            Assert.Multiple(
                () => Assert.False(result.IsValid),
                () => Assert.Contains(
                    result.Errors,
                    error => error.Contains(
                        "Duplicate tool name 'analyze_sample_code'", StringComparison.Ordinal)));
        }

        [Fact]
        public void Should_report_a_duplicate_parameter_name()
        {
            // Arrange — a tool declares the same parameter key twice.
            var registry =
                """
                {
                  "schemaVersion": "1",
                  "tools": [
                    {
                      "name": "analyze_sample_code",
                      "workerId": "dotnet",
                      "description": "Analyse sample source.",
                      "parameters": {
                        "content": { "type": "string", "description": "First." },
                        "content": { "type": "string", "description": "Second." }
                      }
                    }
                  ]
                }
                """;

            // Act
            var result = McpToolsRegistrySchemaValidator.Validate(
                registry, McpToolsRegistryTestFiles.SchemaJson);

            // Assert
            Assert.Multiple(
                () => Assert.False(result.IsValid),
                () => Assert.Contains(
                    result.Errors,
                    error => error.Contains(
                        "Duplicate parameter name 'content'", StringComparison.Ordinal)));
        }

        [Fact]
        public void Should_report_malformed_registry_json()
        {
            // Act
            var result = McpToolsRegistrySchemaValidator.Validate(
                "not json", McpToolsRegistryTestFiles.SchemaJson);

            // Assert
            Assert.Multiple(
                () => Assert.False(result.IsValid),
                () => Assert.Contains(
                    result.Errors,
                    error => error.Contains("not valid JSON", StringComparison.Ordinal)));
        }

        [Fact]
        public void Should_reject_null_arguments()
        {
            // Act + Assert
            Assert.Multiple(
                () => Assert.Throws<ArgumentNullException>(
                    () => McpToolsRegistrySchemaValidator.Validate(
                        null!, McpToolsRegistryTestFiles.SchemaJson)),
                () => Assert.Throws<ArgumentNullException>(
                    () => McpToolsRegistrySchemaValidator.Validate(
                        McpToolsRegistryTestFiles.RegistryJson, null!)));
        }
    }
}
