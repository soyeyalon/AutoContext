namespace AutoContext.Engine.Core.Tests.Features.McpTools;

using AutoContext.Engine.Core.Features.McpTools;
using AutoContext.Engine.Core.Tests.Support.Features.McpTools;
using AutoContext.Engine.Tests.Support.IO;

public sealed class McpToolsRegistryLoaderTests
{
    public sealed class LoadAsync(TempDirectoryFixture tempDirectory)
        : IClassFixture<TempDirectoryFixture>
    {
        [Fact]
        public async Task Should_map_tools_in_document_order()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            Assert.Collection(
                registry.Tools,
                first => Assert.Equal("analyze_csharp_code_style", first.Name),
                second => Assert.Equal("read_editorconfig_rules", second.Name));
        }

        [Fact]
        public async Task Should_map_tool_fields()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            var tool = registry.FindByName("analyze_csharp_code_style");

            Assert.NotNull(tool);
            Assert.Multiple(
                () => Assert.Equal("dotnet", tool.WorkerId),
                () => Assert.Equal("Analyse sample source.", tool.ModelDescription),
                () => Assert.Equal(["csharp_indent_size"], tool.Editorconfig));
        }

        [Fact]
        public async Task Should_map_declared_worker_tasks_in_order()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            var tool = registry.FindByName("analyze_csharp_code_style");

            Assert.NotNull(tool);
            Assert.Multiple(
                () => Assert.Equal(["analyze_csharp_coding_style", "analyze_csharp_async_patterns"], tool.Tasks),
                () => Assert.Equal(["analyze_csharp_coding_style", "analyze_csharp_async_patterns"], tool.ResolvedTasks));
        }

        [Fact]
        public async Task Should_resolve_a_tool_without_declared_tasks_to_its_own_name()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            var tool = registry.FindByName("read_editorconfig_rules");

            Assert.NotNull(tool);
            Assert.Multiple(
                () => Assert.Empty(tool.Tasks),
                () => Assert.Equal(["read_editorconfig_rules"], tool.ResolvedTasks));
        }

        [Fact]
        public async Task Should_merge_catalog_category_and_display_description()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            var tool = registry.FindByName("analyze_csharp_code_style");

            Assert.NotNull(tool);
            Assert.Multiple(
                () => Assert.Equal("C#", tool.Category),
                () => Assert.Equal("Analyse sample source.", tool.ModelDescription),
                () => Assert.Contains(
                    "coding style", tool.DisplayDescription, StringComparison.Ordinal));
        }

        [Fact]
        public async Task Should_flatten_activation_flags_from_category_ancestry()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            var nested = registry.FindByName("analyze_csharp_code_style");
            var root = registry.FindByName("read_editorconfig_rules");

            Assert.NotNull(nested);
            Assert.NotNull(root);
            Assert.Multiple(
                () => Assert.Equal(["hasDotNet", "hasCSharp"], nested.ActivationFlags),
                () => Assert.Empty(root.ActivationFlags));
        }

        [Fact]
        public async Task Should_expose_catalog_categories_with_parent_and_flags()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            var csharp = Assert.Single(
                registry.Categories, category => category.Name == "C#");

            Assert.Multiple(
                () => Assert.Equal(".NET", csharp.Parent),
                () => Assert.Equal(["hasDotNet", "hasCSharp"], csharp.ActivationFlags));
        }

        [Fact]
        public async Task Should_map_parameters_in_declaration_order_with_required_flag()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            var tool = registry.FindByName("analyze_csharp_code_style");

            Assert.NotNull(tool);
            Assert.Collection(
                tool.Parameters,
                first => Assert.Multiple(
                    () => Assert.Equal("content", first.Name),
                    () => Assert.Equal("string", first.Type),
                    () => Assert.Equal("The source text.", first.Description),
                    () => Assert.True(first.Required)),
                second => Assert.Multiple(
                    () => Assert.Equal("maxIssues", second.Name),
                    () => Assert.Equal("number", second.Type),
                    () => Assert.False(second.Required)));
        }

        [Fact]
        public async Task Should_default_editorconfig_to_empty_when_absent()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            var tool = registry.FindByName("read_editorconfig_rules");

            Assert.NotNull(tool);
            Assert.Empty(tool.Editorconfig);
        }

        [Fact]
        public async Task Should_return_null_from_find_for_unknown_tool_name()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteValid(directory);

            // Act
            var registry = await McpToolsRegistryLoader.LoadAsync(
                directory, TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(registry.FindByName("missing_tool"));
        }

        [Fact]
        public async Task Should_throw_when_registry_missing()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalog(
                directory, McpToolsRegistryTestFiles.CatalogJson);
            McpToolsRegistryTestFiles.WriteCatalogSchema(
                directory, McpToolsRegistryTestFiles.CatalogSchemaJson);
            McpToolsRegistryTestFiles.WriteSchema(
                directory, McpToolsRegistryTestFiles.SchemaJson);

            // Act + Assert
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Should_throw_when_schema_missing()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalog(
                directory, McpToolsRegistryTestFiles.CatalogJson);
            McpToolsRegistryTestFiles.WriteCatalogSchema(
                directory, McpToolsRegistryTestFiles.CatalogSchemaJson);
            McpToolsRegistryTestFiles.WriteRegistry(
                directory, McpToolsRegistryTestFiles.RegistryJson);

            // Act + Assert
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Should_throw_when_catalog_missing()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalogSchema(
                directory, McpToolsRegistryTestFiles.CatalogSchemaJson);
            McpToolsRegistryTestFiles.WriteRegistry(
                directory, McpToolsRegistryTestFiles.RegistryJson);
            McpToolsRegistryTestFiles.WriteSchema(
                directory, McpToolsRegistryTestFiles.SchemaJson);

            // Act + Assert
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Should_throw_when_catalog_schema_missing()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalog(
                directory, McpToolsRegistryTestFiles.CatalogJson);
            McpToolsRegistryTestFiles.WriteRegistry(
                directory, McpToolsRegistryTestFiles.RegistryJson);
            McpToolsRegistryTestFiles.WriteSchema(
                directory, McpToolsRegistryTestFiles.SchemaJson);

            // Act + Assert
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Should_throw_when_registry_is_not_valid_json()
        {
            // Arrange
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalog(
                directory, McpToolsRegistryTestFiles.CatalogJson);
            McpToolsRegistryTestFiles.WriteCatalogSchema(
                directory, McpToolsRegistryTestFiles.CatalogSchemaJson);
            McpToolsRegistryTestFiles.WriteRegistry(directory, "not json");
            McpToolsRegistryTestFiles.WriteSchema(
                directory, McpToolsRegistryTestFiles.SchemaJson);

            // Act + Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Should_throw_when_registry_fails_schema_validation()
        {
            // Arrange — the tool name violates the snake_case pattern.
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalog(
                directory, McpToolsRegistryTestFiles.CatalogJson);
            McpToolsRegistryTestFiles.WriteCatalogSchema(
                directory, McpToolsRegistryTestFiles.CatalogSchemaJson);
            McpToolsRegistryTestFiles.WriteRegistry(
                directory,
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
                """);
            McpToolsRegistryTestFiles.WriteSchema(
                directory, McpToolsRegistryTestFiles.SchemaJson);

            // Act + Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Should_throw_when_catalog_fails_schema_validation()
        {
            // Arrange - the tool name violates the snake_case pattern.
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalog(
                directory,
                """
                {
                  "schemaVersion": "1",
                  "categories": [
                    {
                      "name": "Workspace",
                      "description": "Workspace-level tools.",
                      "workerId": "workspace"
                    }
                  ],
                  "tools": [
                    {
                      "name": "AnalyzeGitCommit",
                      "description": "Validate commit messages.",
                      "category": "Workspace"
                    }
                  ]
                }
                """);
            McpToolsRegistryTestFiles.WriteCatalogSchema(
                directory, McpToolsRegistryTestFiles.CatalogSchemaJson);
            McpToolsRegistryTestFiles.WriteRegistry(
                directory, McpToolsRegistryTestFiles.RegistryJson);
            McpToolsRegistryTestFiles.WriteSchema(
                directory, McpToolsRegistryTestFiles.SchemaJson);

            // Act + Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Should_throw_when_registry_tool_has_no_catalog_entry()
        {
            // Arrange - the registry tool name is absent from the catalog.
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalog(
                directory, McpToolsRegistryTestFiles.CatalogJson);
            McpToolsRegistryTestFiles.WriteCatalogSchema(
                directory, McpToolsRegistryTestFiles.CatalogSchemaJson);
            McpToolsRegistryTestFiles.WriteRegistry(
                directory,
                """
                {
                  "schemaVersion": "1",
                  "tools": [
                    {
                      "name": "analyze_unlisted_tool",
                      "workerId": "dotnet",
                      "description": "Not in the catalog.",
                      "parameters": {
                        "content": { "type": "string", "description": "Source.", "required": true }
                      }
                    }
                  ]
                }
                """);
            McpToolsRegistryTestFiles.WriteSchema(
                directory, McpToolsRegistryTestFiles.SchemaJson);

            // Act + Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Should_throw_when_workerid_conflicts_with_catalog_category()
        {
            // Arrange - the registry worker disagrees with the catalog category's worker.
            var directory = tempDirectory.CreateDirectory();
            McpToolsRegistryTestFiles.WriteCatalog(
                directory, McpToolsRegistryTestFiles.CatalogJson);
            McpToolsRegistryTestFiles.WriteCatalogSchema(
                directory, McpToolsRegistryTestFiles.CatalogSchemaJson);
            McpToolsRegistryTestFiles.WriteRegistry(
                directory,
                """
                {
                  "schemaVersion": "1",
                  "tools": [
                    {
                      "name": "analyze_csharp_code_style",
                      "workerId": "workspace",
                      "description": "Mismatched worker.",
                      "parameters": {
                        "content": { "type": "string", "description": "Source.", "required": true }
                      }
                    }
                  ]
                }
                """);
            McpToolsRegistryTestFiles.WriteSchema(
                directory, McpToolsRegistryTestFiles.SchemaJson);

            // Act + Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Should_reject_blank_resources_directory(string directory)
        {
            // Act + Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => McpToolsRegistryLoader.LoadAsync(
                    directory, TestContext.Current.CancellationToken));
        }
    }
}
