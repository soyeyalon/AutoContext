namespace AutoContext.Instructions.Manifest.Generator;

using System.Text;
using System.Text.Json;

/// <inheritdoc cref="IMcpToolsRegistryReader" />
internal sealed class McpToolsRegistryReader : IMcpToolsRegistryReader
{
    private const string RegistryLabel = "mcp-tools-registry.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, McpToolsRegistryTool> Read(string registryPath)
    {
        ArgumentNullException.ThrowIfNull(registryPath);

        if (!File.Exists(registryPath))
        {
            throw Fail("registry file not found at '" + registryPath + "'");
        }

        using var document = Parse(File.ReadAllText(registryPath, Utf8NoBom));

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            throw Fail("registry is missing its `tools` array");
        }

        var registered = new Dictionary<string, McpToolsRegistryTool>(StringComparer.Ordinal);

        foreach (var tool in tools.EnumerateArray())
        {
            var entry = ReadTool(tool);

            if (!registered.TryAdd(entry.Name, entry))
            {
                throw Fail("duplicate tool '" + entry.Name + "'");
            }
        }

        return registered;
    }

    private static InvalidOperationException Fail(string message)
        => new("[" + RegistryLabel + "] " + message);

    private static JsonDocument Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw Fail("registry is not valid JSON: " + exception.Message);
        }
    }

    private static McpToolsRegistryTool ReadTool(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object
            || !tool.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            throw Fail("a tool has a missing or blank `name`");
        }

        var name = nameElement.GetString()!;
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);

        if (tool.TryGetProperty("parameters", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw Fail("tool '" + name + "' has a `parameters` value that is not an object");
            }

            foreach (var parameter in parameters.EnumerateObject())
            {
                parameterNames.Add(parameter.Name);
            }
        }

        return new McpToolsRegistryTool(name, parameterNames);
    }
}
