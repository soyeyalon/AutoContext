namespace AutoContext.Engine.Core.Features.McpTools.Format;

/// <summary>
/// Disk read-model for one entry of the <c>tools</c> array in
/// <c>mcp-tools-registry.json</c> — a single MCP tool the engine
/// advertises. Mirrors the <c>toolDefinition</c> shape in
/// <c>mcp-tools-registry.schema.json</c>: the model-facing
/// <see cref="Description"/> and <see cref="Parameters"/> contract plus
/// the <see cref="WorkerId"/> dispatch target and the optional
/// <see cref="Editorconfig"/> keys the engine resolves before invoking
/// the worker.
/// </summary>
/// <param name="Name">The MCP tool name (snake_case); unique across the
/// registry.</param>
/// <param name="WorkerId">Identifier of the worker that runs the tool;
/// foreign key into <c>workers.json</c>.</param>
/// <param name="Description">The model-facing tool description surfaced
/// over MCP <c>tools/list</c>.</param>
/// <param name="Parameters">The parameter map (camelCase name → spec);
/// at least one entry.</param>
/// <param name="Editorconfig">The EditorConfig keys the tool consumes, or
/// <see langword="null"/> when it consumes none.</param>
/// <param name="Tasks">The worker task names the tool runs, in order, or
/// <see langword="null"/> when the tool's own name is its single task.</param>
internal sealed record JsonMcpToolsRegistryTool(
    string? Name = null,
    string? WorkerId = null,
    string? Description = null,
    IReadOnlyDictionary<string, JsonMcpToolsRegistryParameter>? Parameters = null,
    IReadOnlyList<string>? Editorconfig = null,
    IReadOnlyList<string>? Tasks = null);
