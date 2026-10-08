namespace AutoContext.Engine.Core.Tests.Support.Features.McpTools;

using AutoContext.Engine.Core.Features.McpTools;

/// <summary>
/// Shared MCP-tools registry side-car fixtures for the loader and validator
/// tests: a hand-authored <c>mcp-tools-registry.json</c> with two tools
/// across two workers (one declaring a required and an optional parameter
/// plus an EditorConfig key, the other a single optional parameter and no
/// EditorConfig keys), validated against the <em>real</em> bundled
/// <c>mcp-tools-registry.schema.json</c>. The tool names are real entries in
/// the bundled <c>mcp-tools-catalog.json</c> (read from the linked copy) so
/// the loader's registry/catalog merge resolves each tool to its category.
/// The schema and catalog are read from the copies the build links into the
/// test output from <c>src/AutoContext.Engine/Resources</c>, so the tests can
/// never drift from the shipped contracts.
/// </summary>
internal static class McpToolsRegistryTestFiles
{
    public static string CatalogJson { get; } = File.ReadAllText(
        Path.Combine(
            AppContext.BaseDirectory, "Resources", McpToolsRegistryLoader.CatalogFileName));

    public const string RegistryJson =
        """
        {
          "$schema": "./mcp-tools-registry.schema.json",
          "schemaVersion": "1",
          "tools": [
            {
              "name": "analyze_csharp_code_style",
              "workerId": "dotnet",
              "tasks": [ "analyze_csharp_coding_style", "analyze_csharp_async_patterns" ],
              "description": "Analyse sample source.",
              "parameters": {
                "content": { "type": "string", "description": "The source text.", "required": true },
                "maxIssues": { "type": "number", "description": "Issue cap." }
              },
              "editorconfig": [ "csharp_indent_size" ]
            },
            {
              "name": "read_editorconfig_rules",
              "workerId": "workspace",
              "description": "Read sample config.",
              "parameters": {
                "filePath": { "type": "string", "description": "Absolute path." }
              }
            }
          ]
        }
        """;

    /// <summary>
    /// The real bundled registry JSON Schema, read from the copy the build
    /// links into the test output. Single source of truth: there is no
    /// embedded duplicate to fall out of step with the shipped schema.
    /// </summary>
    public static string SchemaJson { get; } = File.ReadAllText(
        Path.Combine(
            AppContext.BaseDirectory, "Resources", McpToolsRegistryLoader.SchemaFileName));

    public static string CatalogSchemaJson { get; } = File.ReadAllText(
        Path.Combine(
            AppContext.BaseDirectory, "Resources", McpToolsRegistryLoader.CatalogSchemaFileName));

    public static void WriteValid(string directory)
    {
        WriteCatalog(directory, CatalogJson);
        WriteCatalogSchema(directory, CatalogSchemaJson);
        WriteRegistry(directory, RegistryJson);
        WriteSchema(directory, SchemaJson);
    }

    public static void WriteCatalog(string directory, string json)
        => File.WriteAllText(
            Path.Combine(directory, McpToolsRegistryLoader.CatalogFileName), json);

    public static void WriteCatalogSchema(string directory, string json)
        => File.WriteAllText(
            Path.Combine(directory, McpToolsRegistryLoader.CatalogSchemaFileName), json);

    public static void WriteRegistry(string directory, string json)
        => File.WriteAllText(
            Path.Combine(directory, McpToolsRegistryLoader.RegistryFileName), json);

    public static void WriteSchema(string directory, string json)
        => File.WriteAllText(
            Path.Combine(directory, McpToolsRegistryLoader.SchemaFileName), json);
}
