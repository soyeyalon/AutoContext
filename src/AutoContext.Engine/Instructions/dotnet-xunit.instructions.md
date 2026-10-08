---
name: "dotnet-xunit (v1.0.0)"
description: "Apply when writing or reviewing xUnit tests, test doubles, or xUnit-specific APIs in .NET."
applyTo: "**/*Tests*.{cs,fs,vb,razor}"
---

# xUnit Instructions

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

- [INST0001] **Do** use **xUnit v3** with `[Fact]` / `[Theory]` attributes and constructor injection for test setup.
- [INST0002] **Do** use `[Theory]` + `[InlineData]` for simple parameterised cases and `[MemberData]` or `[ClassData]` for complex or shared test data.
- [INST0003] **Do** use `await Assert.ThrowsAsync<T>()` for async exceptions.
- [INST0004] **Do** use `Assert.Multiple()` when a test needs more than one assertion, so all failures are reported together.
- [INST0005] **Do** use `Assert.Skip()` to conditionally skip tests at runtime instead of commenting them out or using conditional compilation.
- [INST0006] **Do** use `IAsyncLifetime` for async setup/teardown — `InitializeAsync` and `DisposeAsync` return `ValueTask` in v3.
- [INST0007] **Do** use fixtures at the right scope: `IClassFixture<T>` for a single class, `ICollectionFixture<T>` for multiple classes, and `[AssemblyFixture]` for assembly-wide shared state.
- [INST0008] **Do** log diagnostics via `ITestOutputHelper` (in `Xunit.Sdk`) or access test metadata via `TestContext`.
- [INST0009] **Don't** call `.ConfigureAwait(...)` inside test methods (xUnit1030).
- [INST0010] **Don't** use `async void` test methods — `AsyncTestSyncContext` was removed in v3; always return `Task` or `ValueTask`.
