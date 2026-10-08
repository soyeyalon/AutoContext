---
name: "dotnet-coding-standards (v1.0.0)"
description: "Apply when writing or reviewing .NET code (design guidelines, naming, input validation, file organization, API design)."
applyTo: "**/*.{cs,fs,vb}"
---

# .NET Coding Standards Instructions

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

### General

- [INST0001] **Do** follow .NET design guidelines and common conventions unless noted below.
- [INST0002] **Do** use current .NET features when they deliver measurable performance gains.
- [INST0003] **Do** prefer source-generated APIs over their legacy runtime-reflection counterparts — `[LibraryImport]` over `[DllImport]`, `[GeneratedRegex]` over `new Regex(…)`, `[LoggerMessage]` over manual log delegates, and `[JsonSerializable]` source-generated contexts over reflection-based `System.Text.Json` — they are trim- and AOT-safe, catch errors at compile time, and eliminate runtime code-generation overhead.
- [INST0004] **Do** validate inputs at system boundaries.
- [INST0005] **Do** use `ArgumentException.ThrowIf*`, `ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException.ThrowIf*` and similar static throw helpers for precondition checks.
- [INST0006] **Do** follow the cross-platform comment-content rule in [design-principles#INST0008] — comments explain *what* (API docs) or *why* (inline), never *how*. For .NET XML docs specifically, that means `<summary>` describes the contract, with rationale moving into `<remarks>` when it adds value.
- [INST0007] **Don't** catch `System.Exception` (or `System.SystemException`) unless you immediately rethrow it — only catch the most specific exception type you can meaningfully handle.
- [INST0008] **Don't** use an empty or log-only `catch` block when you can use an exception filter (`when`) instead — `catch (IOException e) when (e.HResult == ...)` preserves the original stack trace and avoids swallowing unrelated exceptions.
- [INST0009] **Don't** call `Console.*` or `Debug.*`; use `ILogger` or `Serilog` in production and `ITestOutputHelper` in tests.

### Naming Conventions

- [INST0010] **Do** give descriptive names that convey intent — prefer `retriesRemaining` over `n` and `GetUserById` over `GetUser`.
- [INST0011] **Do** prefix interfaces with `I` (e.g., `IMyType`).
- [INST0012] **Do** suffix attribute types with `Attribute` (e.g., `SerializableAttribute`).
- [INST0013] **Do** suffix extension classes with `Extensions` (e.g., `MyTypeExtensions`).
- [INST0014] **Do** suffix async methods with `Async` (e.g., `GetDataAsync()`).
- [INST0015] **Do** use a singular noun for non-flags enum types and a plural noun for flags enum types (e.g., `FileMode` vs `FileAttributes`).
- [INST0016] **Do** name Boolean properties with an affirmative phrase — optionally prefix with `Is`, `Can`, or `Has` when it adds clarity (e.g., `IsEnabled`, `CanSeek`, `HasChildren`).

### File & Project Organization

- [INST0017] **Do** keep a single type per file and name the file after that type (e.g., `User.cs` for `User` class).
- [INST0018] **Do** place each type in a folder that reflects its role and namespace, naming the folder after the namespace.
- [INST0019] **Do** run `dotnet format`, then `dotnet build`, then `dotnet test` after making changes — fix build errors before addressing test failures.
- [INST0020] **Do** prefer built-in formatting rules (e.g., `IDEXXXXX`, `dotnet`, `csharp`) over StyleCop when they provide sufficient coverage.

### Immutability & API Surface

- [INST0021] **Do** prefer `readonly` fields and `init`-only properties; use `record` for data-carrying types — immutability eliminates accidental mutation bugs.
- [INST0022] **Do** return `IReadOnlyCollection<T>` or `IEnumerable<T>` from public APIs instead of `List<T>` or other mutable concrete types — callers should not depend on mutability you did not intend.
- [INST0023] **Do** enforce preconditions on every `public`, `protected`, and `internal` member with the `ThrowIf*` helpers from [#INST0005] (the .NET form of [design-principles#INST0010]). For invariants not reducible to a parameter check, throw `InvalidOperationException` naming the violated condition.
- [INST0024] **Do** keep constructors total and side-effect-free — they should only validate arguments (via the `ThrowIf*` helpers, [#INST0005]) and assign fields. Push asynchronous work, I/O, and any operation that can fail for reasons other than bad arguments into a `static Task<T> CreateAsync(...)` factory; mark the constructor `private` or `internal` so callers cannot bypass the factory. For types that need progressive configuration, return the finished instance from a builder's `Build()`; do not expose a partially-built type as the public surface.
- [INST0025] **Don't** expose internal implementation details in public APIs — and don't widen visibility (`private` → `internal`/`protected`/`public`), apply `[InternalsVisibleTo]` on a production assembly (e.g., `[InternalsVisibleTo("MyApp.Tests")]`) to grant test assemblies access to its internals, or add test-only members/setters to reach private state. Hide implementation behind interfaces or abstractions, and where tests can't reach a behavior through the public API, refactor the design (see [design-principles#INST0015]) rather than the visibility.
- [INST0026] **Don't** declare `const` with any visibility other than `private`, except when the value must be a compile-time constant. Keep implementation constants private to the type that uses them. If the value must be shared, expose it from the owning type as a `static readonly` field or get-only property; don't use `public const` for values that may ever change. Across assemblies, `public const` is inlined into consumers at compile time, so updates are not observed until every consumer recompiles. Use non-`private const` only for genuine axioms, such as `Math.PI` or protocol-fixed sentinels, or for attribute arguments where `const` is required. Codebase-chosen values, e.g., timeouts, retry counts, buffer sizes, and default paths, are not axioms. See also [design-principles#INST0007] for when a constant should be exposed at all.
