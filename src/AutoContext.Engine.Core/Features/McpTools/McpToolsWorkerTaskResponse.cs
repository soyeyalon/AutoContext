namespace AutoContext.Engine.Core.Features.McpTools;

using System.Text.Json;

/// <summary>
/// One worker task's reply to a tool invocation, read off the wire before
/// <see cref="McpToolsInvoker"/> merges every task's reply into the tool's
/// single result. A tool runs one or more worker tasks
/// (<see cref="Snapshot.McpToolsRegistryEntry.ResolvedTasks"/>), so the
/// invoker collects one of these per task.
/// </summary>
/// <param name="TaskName">The worker task that replied.</param>
/// <param name="Status">The reply's <c>status</c> (<c>ok</c> or
/// <c>error</c>), or <see langword="null"/> when the reply carried none.</param>
/// <param name="Output">The task's <c>output</c> payload, or
/// <see langword="null"/> when absent.</param>
/// <param name="Error">The task's <c>error</c> message, or
/// <see langword="null"/> when absent.</param>
internal sealed record McpToolsWorkerTaskResponse(
    string TaskName,
    string? Status,
    JsonElement? Output,
    string? Error);
