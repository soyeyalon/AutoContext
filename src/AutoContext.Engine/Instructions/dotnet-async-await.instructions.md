---
name: "dotnet-async-await (v1.0.0)"
description: "Apply when writing or reviewing async .NET code (Task/ValueTask, CancellationToken, IAsyncEnumerable, IAsyncDisposable, ConfigureAwait)."
applyTo: "**/*.{cs,fs,vb}"
---

# Async / Await Instructions

## MCP Tool Validation

After editing or generating any C# source file, call the
`analyze_csharp_code_style` and `analyze_csharp_project_structure`
MCP tools on the changed source. Pass the file contents as `content`
and the file's absolute path as `filePath`. For test files, also call
`analyze_csharp_testing_style` with the same `content` and `filePath`,
plus the test project's directory as `projectDirectory`. Treat any
reported violation as blocking — fix it before reporting the work as
done.

## Rules

- [INST0001] **Do** write true `async`/`await` code, don't mix sync and async code; follow the async all the way down.
- [INST0002] **Do** add an optional `CancellationToken ct = default` as the final parameter in public async APIs.
- [INST0003] **Do** use `IAsyncEnumerable<T>` for streaming operations (e.g., `await foreach (var row in repo.GetRowsAsync()) {}`).
- [INST0004] **Do** use `ValueTask` only when best practices permit and profiling shows a measurable benefit over `Task`.
- [INST0005] **Do** implement `IAsyncDisposable` for async cleanup.
- [INST0006] **Do** add `.ConfigureAwait(false)` to awaits in library code — code other projects consume, which may run under a captured synchronization context — to avoid deadlocks; application code (console, web, worker services) and test code have no context worth avoiding and don't need it, and xUnit tests must not use it (xUnit1030).
- [INST0007] **Don't** use `async void` except for event handlers—unobserved exceptions crash the process.
- [INST0008] **Don't** block on async code by calling `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` on a `Task` — this deadlocks in UI / ASP.NET contexts and defeats all back-pressure from the async pipeline.
