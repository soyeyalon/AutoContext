namespace AutoContext.Engine.Core.Features.McpTools;

using System.Text.Json;

using AutoContext.Engine.Core.Features.McpTools.Format;
using AutoContext.Engine.Core.Features.McpTools.Snapshot;
using AutoContext.Engine.Core.Infrastructure;

/// <summary>
/// Loads the bundled MCP-tools registry into an immutable
/// <see cref="McpToolsRegistry"/> by reading the side-cars shipped
/// beside the engine binary: <c>mcp-tools-registry.json</c> (the
/// hand-authored execution registry) and its
/// <c>mcp-tools-registry.schema.json</c> (the validation contract), plus
/// <c>mcp-tools-catalog.json</c> and
/// <c>mcp-tools-catalog.schema.json</c> to ensure the UI catalog side is
/// also package-valid at startup.
/// </summary>
/// <remarks>
/// The side-cars are engine build artifacts, not user input: a missing
/// file, malformed JSON, a registry that fails schema or cross-reference
/// validation, or a row missing a required field is a packaging defect, so
/// the loader throws rather than degrading to a partial registry. Failing
/// here fails engine startup loudly, which is the intended behaviour. The
/// registry is validated against the schema <em>before</em> it is mapped,
/// so the field-presence guards in the mapping are belt-and-braces.
/// </remarks>
internal static class McpToolsRegistryLoader
{
    /// <summary>The hand-authored execution-registry side-car file name.</summary>
    public const string RegistryFileName = "mcp-tools-registry.json";

    /// <summary>The registry's JSON Schema side-car file name.</summary>
    public const string SchemaFileName = "mcp-tools-registry.schema.json";

    /// <summary>The hand-authored MCP-tools catalog side-car file name.</summary>
    public const string CatalogFileName = "mcp-tools-catalog.json";

    /// <summary>The catalog's JSON Schema side-car file name.</summary>
    public const string CatalogSchemaFileName = "mcp-tools-catalog.schema.json";

    /// <summary>
    /// Reads and validates the registry side-car from
    /// <paramref name="resources"/> and maps it to an immutable snapshot.
    /// The catalog side-cars are also read and schema-validated so startup
    /// fails fast when either half of the MCP-tools manifest set is
    /// malformed.
    /// </summary>
    /// <param name="resources">The resources directory holding all four
    /// side-cars (override copies shadow the bundled ones). Must not be
    /// <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the file reads.</param>
    /// <returns>The loaded, immutable registry snapshot.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resources"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">A side-car is
    /// missing.</exception>
    /// <exception cref="InvalidOperationException">The registry is
    /// malformed, fails validation, or a row is missing a required
    /// field.</exception>
    public static async Task<McpToolsRegistry> LoadAsync(
        EngineResourcesDirectory resources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var registryPath = resources.ResolveFile(RegistryFileName);
        var schemaPath = resources.ResolveFile(SchemaFileName);
        var catalogPath = resources.ResolveFile(CatalogFileName);
        var catalogSchemaPath = resources.ResolveFile(CatalogSchemaFileName);

        var registryJson = await ReadTextAsync(registryPath, cancellationToken).ConfigureAwait(false);
        var schemaJson = await ReadTextAsync(schemaPath, cancellationToken).ConfigureAwait(false);
        var catalogJson = await ReadTextAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        var catalogSchemaJson = await ReadTextAsync(catalogSchemaPath, cancellationToken)
            .ConfigureAwait(false);

        var validation = McpToolsRegistrySchemaValidator.Validate(registryJson, schemaJson);

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Bundled MCP-tools registry '{registryPath}' failed validation:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, validation.Errors));
        }

        var catalogValidation = McpToolsCatalogSchemaValidator.Validate(catalogJson, catalogSchemaJson);

        if (!catalogValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"Bundled MCP-tools catalog '{catalogPath}' failed validation:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, catalogValidation.Errors));
        }

        var registry = Deserialize(
            registryJson, McpToolsRegistryJsonContext.Default.JsonMcpToolsRegistry, registryPath);
        var catalog = Deserialize(
            catalogJson, McpToolsCatalogJsonContext.Default.JsonMcpToolsCatalog, catalogPath);

        return Merge(registry, catalog, registryPath, catalogPath);
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Bundled MCP-tools side-car not found: '{path}'.", path);
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static T Deserialize<T>(
        string json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string path)
        where T : class
    {
        T? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Bundled MCP-tools side-car '{path}' is not valid JSON.", exception);
        }

        return parsed ?? throw Malformed(path, "it deserialised to null.");
    }

    private static McpToolsRegistry Merge(
        JsonMcpToolsRegistry registry,
        JsonMcpToolsCatalog catalog,
        string registryPath,
        string catalogPath)
    {
        var categoryRows = catalog.Categories
            ?? throw Malformed(catalogPath, "the 'categories' array is missing.");

        var categoriesByName = IndexCategories(categoryRows, catalogPath);
        var resolvedCategories = ResolveCategories(categoryRows, categoriesByName, catalogPath);
        var catalogToolsByName = IndexCatalogTools(catalog.Tools, categoriesByName, catalogPath);

        var categories = new List<McpToolsCategoryEntry>(categoryRows.Count);

        foreach (var row in categoryRows)
        {
            var name = Required(row.Name, "categories[].name", catalogPath);

            categories.Add(new McpToolsCategoryEntry
            {
                Name = name,
                Description = Required(
                    row.Description, $"categories[].description for '{name}'", catalogPath),
                Parent = row.Parent,
                ActivationFlags = resolvedCategories[name].Flags,
            });
        }

        var registryRows = registry.Tools
            ?? throw Malformed(registryPath, "the 'tools' array is missing.");

        var tools = new List<McpToolsRegistryEntry>(registryRows.Count);

        foreach (var row in registryRows)
        {
            tools.Add(MergeTool(
                row, catalogToolsByName, resolvedCategories, registryPath, catalogPath));
        }

        return new McpToolsRegistry(categories, tools);
    }

    private static Dictionary<string, JsonMcpToolsCatalogCategory> IndexCategories(
        IReadOnlyList<JsonMcpToolsCatalogCategory> rows,
        string catalogPath)
    {
        var byName = new Dictionary<string, JsonMcpToolsCatalogCategory>(
            rows.Count, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var name = Required(row.Name, "categories[].name", catalogPath);

            if (!byName.TryAdd(name, row))
            {
                throw Malformed(
                    catalogPath, $"the category name '{name}' appears more than once.");
            }
        }

        return byName;
    }

    private static Dictionary<string, (IReadOnlyList<string> Flags, string? WorkerId)> ResolveCategories(
        IReadOnlyList<JsonMcpToolsCatalogCategory> rows,
        IReadOnlyDictionary<string, JsonMcpToolsCatalogCategory> byName,
        string catalogPath)
    {
        var resolved = new Dictionary<string, (IReadOnlyList<string>, string?)>(
            rows.Count, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var name = Required(row.Name, "categories[].name", catalogPath);
            var chain = AncestryRootToLeaf(name, byName, catalogPath);

            var flags = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? workerId = null;

            foreach (var ancestor in chain)
            {
                foreach (var flag in ancestor.ActivationFlags ?? [])
                {
                    if (seen.Add(flag))
                    {
                        flags.Add(flag);
                    }
                }

                if (!string.IsNullOrEmpty(ancestor.WorkerId))
                {
                    workerId = ancestor.WorkerId;
                }
            }

            resolved[name] = (flags, workerId);
        }

        return resolved;
    }

    private static List<JsonMcpToolsCatalogCategory> AncestryRootToLeaf(
        string name,
        IReadOnlyDictionary<string, JsonMcpToolsCatalogCategory> byName,
        string catalogPath)
    {
        var chain = new List<JsonMcpToolsCatalogCategory>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = name;

        while (current is not null)
        {
            if (!visited.Add(current))
            {
                throw Malformed(catalogPath, $"the category '{current}' is part of a parent cycle.");
            }

            if (!byName.TryGetValue(current, out var node))
            {
                throw Malformed(
                    catalogPath, $"the category '{name}' references undefined ancestor '{current}'.");
            }

            chain.Add(node);
            current = node.Parent;
        }

        chain.Reverse();
        return chain;
    }

    private static Dictionary<string, JsonMcpToolsCatalogTool> IndexCatalogTools(
        IReadOnlyList<JsonMcpToolsCatalogTool>? rows,
        Dictionary<string, JsonMcpToolsCatalogCategory> categoriesByName,
        string catalogPath)
    {
        var toolRows = rows
            ?? throw Malformed(catalogPath, "the 'tools' array is missing.");

        var byName = new Dictionary<string, JsonMcpToolsCatalogTool>(
            toolRows.Count, StringComparer.Ordinal);

        foreach (var row in toolRows)
        {
            var name = Required(row.Name, "tools[].name", catalogPath);

            if (!byName.TryAdd(name, row))
            {
                throw Malformed(catalogPath, $"the tool name '{name}' appears more than once.");
            }

            var category = Required(row.Category, $"tools[].category for '{name}'", catalogPath);

            if (!categoriesByName.ContainsKey(category))
            {
                throw Malformed(
                    catalogPath, $"tool '{name}' references undefined category '{category}'.");
            }
        }

        return byName;
    }

    private static McpToolsRegistryEntry MergeTool(
        JsonMcpToolsRegistryTool tool,
        Dictionary<string, JsonMcpToolsCatalogTool> catalogToolsByName,
        Dictionary<string, (IReadOnlyList<string> Flags, string? WorkerId)> resolvedCategories,
        string registryPath,
        string catalogPath)
    {
        var name = Required(tool.Name, "tools[].name", registryPath);

        if (!catalogToolsByName.TryGetValue(name, out var catalogTool))
        {
            throw Malformed(catalogPath, $"the registry tool '{name}' has no catalog entry.");
        }

        var category = Required(
            catalogTool.Category, $"catalog tool '{name}' category", catalogPath);
        var (flags, catalogWorkerId) = resolvedCategories[category];
        var workerId = Required(tool.WorkerId, $"tool '{name}' workerId", registryPath);

        if (catalogWorkerId is not null
            && !string.Equals(catalogWorkerId, workerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Bundled MCP-tools manifests disagree on the worker for tool '{name}': registry "
                + $"'{registryPath}' declares workerId '{workerId}' but catalog '{catalogPath}' "
                + $"resolves category '{category}' to workerId '{catalogWorkerId}'.");
        }

        var parameterRows = tool.Parameters
            ?? throw Malformed(registryPath, $"tool '{name}' has no 'parameters'.");

        var parameters = new List<McpToolsRegistryParameterEntry>(parameterRows.Count);

        foreach (var (parameterName, spec) in parameterRows)
        {
            parameters.Add(new McpToolsRegistryParameterEntry
            {
                Name = parameterName,
                Type = Required(spec.Type, $"tool '{name}' parameter '{parameterName}' type", registryPath),
                Description = Required(
                    spec.Description, $"tool '{name}' parameter '{parameterName}' description", registryPath),
                Required = spec.Required ?? false,
            });
        }

        return new McpToolsRegistryEntry
        {
            Name = name,
            Category = category,
            WorkerId = workerId,
            ModelDescription = Required(tool.Description, $"tool '{name}' description", registryPath),
            DisplayDescription = Required(
                catalogTool.Description, $"catalog tool '{name}' description", catalogPath),
            Parameters = parameters,
            Editorconfig = tool.Editorconfig ?? [],
            Tasks = tool.Tasks ?? [],
            ActivationFlags = flags,
        };
    }

    private static string Required(string? value, string field, string path)
        => string.IsNullOrEmpty(value)
            ? throw Malformed(path, $"required field '{field}' is missing or empty.")
            : value;

    private static InvalidOperationException Malformed(string path, string detail)
        => new($"Bundled MCP-tools side-car '{path}' is malformed: {detail}");
}
