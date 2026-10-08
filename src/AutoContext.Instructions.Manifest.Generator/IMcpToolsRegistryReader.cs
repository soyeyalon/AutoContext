namespace AutoContext.Instructions.Manifest.Generator;

/// <summary>
/// Reads the hand-authored <c>mcp-tools-registry.json</c> into the tool names and
/// parameter names the corpus contract is validated against. The generator never
/// writes the registry and reads nothing else from it — the engine owns the
/// registry's full shape and schema-validates it at its own startup — so this
/// reader deliberately knows only <c>tools[].name</c> and the keys of
/// <c>tools[].parameters</c>. Authoring slips the reader can see (a missing file,
/// malformed JSON, a tool with no name, a duplicated name) are build-fatal,
/// reported with the <c>[mcp-tools-registry.json] …</c> locator the orchestrator
/// maps to exit 1.
/// </summary>
internal interface IMcpToolsRegistryReader
{
    /// <summary>
    /// Reads the registry at <paramref name="registryPath"/>.
    /// </summary>
    /// <param name="registryPath">The <c>mcp-tools-registry.json</c> path.</param>
    /// <returns>The registered tools, keyed by name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registryPath"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The registry is missing or
    /// malformed, or declares an unnamed or duplicated tool.</exception>
    IReadOnlyDictionary<string, McpToolsRegistryTool> Read(string registryPath);
}
