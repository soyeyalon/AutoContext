namespace AutoContext.Instructions.Manifest.Generator;

/// <summary>
/// One tool as the hand-authored <c>mcp-tools-registry.json</c> declares it, reduced
/// to what the corpus contract needs: its <see cref="Name"/> and the set of
/// parameter names it accepts. The registry's descriptions, worker ids, and
/// parameter schemas are the engine's concern; the generator only proves that the
/// corpus cites names the registry defines.
/// </summary>
/// <param name="Name">The registered tool name (<c>tools[].name</c>).</param>
/// <param name="ParameterNames">The declared parameter names (the keys of
/// <c>tools[].parameters</c>); empty when the tool declares none.</param>
internal sealed record McpToolsRegistryTool(string Name, IReadOnlySet<string> ParameterNames);
