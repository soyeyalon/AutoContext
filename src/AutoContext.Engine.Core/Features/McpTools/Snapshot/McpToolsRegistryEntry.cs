namespace AutoContext.Engine.Core.Features.McpTools.Snapshot;

/// <summary>
/// Immutable domain model for a single MCP tool the engine advertises,
/// composed at startup by <see cref="McpToolsRegistryLoader"/> by merging
/// one <c>tools</c> entry of <c>mcp-tools-registry.json</c> (the execution
/// facts: <see cref="WorkerId"/>, <see cref="ModelDescription"/>,
/// <see cref="Parameters"/>, <see cref="Editorconfig"/>) with the matching
/// <c>mcp-tools-catalog.json</c> entry (the curatorial facts:
/// <see cref="Category"/> and <see cref="DisplayDescription"/>). The
/// <see cref="ActivationFlags"/> are flattened once at load time from the
/// tool's category ancestry, so consumers never walk the category tree.
/// </summary>
internal sealed record McpToolsRegistryEntry
{
    /// <summary>
    /// The workspace-context flags that gate activation, flattened from
    /// the tool's <see cref="Category"/> ancestry (e.g. a C# tool resolves
    /// to <c>["hasDotNet", "hasCSharp"]</c>); empty when unconditional.
    /// Precomputed at load time and ANDed by the workspace-context
    /// evaluator.
    /// </summary>
    public IReadOnlyList<string> ActivationFlags { get; init; } = [];

    /// <summary>
    /// The catalog category name this tool belongs to; resolves to a
    /// declared <see cref="McpToolsCategoryEntry.Name"/>.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>The MCP tool name (snake_case); unique across the
    /// registry.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Identifier of the worker that runs the tool; foreign key into
    /// <c>workers.json</c>.
    /// </summary>
    public required string WorkerId { get; init; }

    /// <summary>
    /// The model-facing tool description (from the registry) surfaced over
    /// MCP <c>tools/list</c>.
    /// </summary>
    public required string ModelDescription { get; init; }

    /// <summary>
    /// The human-facing display description (from the catalog),
    /// deliberately independent of <see cref="ModelDescription"/>.
    /// </summary>
    public required string DisplayDescription { get; init; }

    /// <summary>
    /// The tool parameters, in registry declaration order; at least one.
    /// </summary>
    public required IReadOnlyList<McpToolsRegistryParameterEntry> Parameters { get; init; }

    /// <summary>
    /// The EditorConfig keys the tool consumes, in declaration order;
    /// empty when it consumes none.
    /// </summary>
    public IReadOnlyList<string> Editorconfig { get; init; } = [];

    /// <summary>
    /// The worker task names the tool runs, in declaration order. A tool
    /// name is not a task name: one tool may run several worker tasks and
    /// merge their reports. Empty means the tool's own <see cref="Name"/>
    /// is its single task; read <see cref="ResolvedTasks"/> rather than
    /// re-deriving that default.
    /// </summary>
    public IReadOnlyList<string> Tasks { get; init; } = [];

    /// <summary>
    /// The worker task names to dispatch: <see cref="Tasks"/> when declared,
    /// otherwise the tool's own <see cref="Name"/>.
    /// </summary>
    public IReadOnlyList<string> ResolvedTasks => Tasks.Count > 0 ? Tasks : [Name];
}
