---
name: "dotnet-testing (v1.0.0)"
description: "Apply when writing or reviewing .NET test naming, organization, or test-project conventions."
applyTo: "**/*Tests*.{cs,fs,vb,razor}"
---

# .NET Testing Instructions

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

- [INST0001] **Do** distinguish dead code (never called) from test-only code — verify test utilities actually serve a clear purpose before removing them.
- [INST0002] **Don't** add XML doc comments (`/// <summary>`) to test classes or test methods — rely on descriptive names to convey intent.
- [INST0003] **Don't** add `using static` directives to test files. Always call statics through their declaring type (e.g., `SomeFactory.Create(...)`, `Assert.Equal(...)`) so test code stays unambiguous about which type each member belongs to and avoids accidental conflicts with locally-defined helpers, fixtures, or other factories in the same file.

### Naming

- [INST0004] **Do** match the test namespace to the standard .NET convention — `<RootNamespace>` of the test project (or, when `<RootNamespace>` is not set in the `.csproj`, the project filename without extension) plus the folder path to the file, joined with dots (e.g. a file at `Pipes/PipeListenerTests.cs` in a project whose `<RootNamespace>` is `MyApp.Tests` belongs in namespace `MyApp.Tests.Pipes`).
- [INST0005] **Do** suffix every test class with `Tests` and prefix every test method with `Should_` or `Should_not_` (e.g., `Should_do_something`, `Should_not_do_something`).
- [INST0006] **Do** name integration tests after the most dependent type (e.g., say `VirtualCodeEditor` depends on `SyntaxHighlighter` then `VirtualCodeEditorTests`).
- [INST0007] **Don't** give tests arbitrary names (e.g., `DebugSomeType`, `AnotherTypeEssentialTests`); always name them `<UnitUnderTest>Tests` (e.g., `SyntaxHighlighterTests`, `VirtualCodeEditorTests`).

### Layout

- [INST0008] **Don't** mix UI tests (e.g., Selenium, Playwright) into unit test projects — keep them in separate test projects.
- [INST0009] **Don't** add a new test class when an existing one already targets the same unit — extend it instead.

### Test Support

- [INST0010] **Do** extract test-support code that's reused across multiple test projects into a dedicated `<ProductionProject>.Tests.Support` project that mirrors the same feature/domain structure as the production code (e.g., shared support across `AutoContext.Framework.*.Tests` lives in `AutoContext.Framework.Tests.Support`).
- [INST0011] **Do** place reusable test-support code that belongs to a single test project inside a `Support/` folder at the test-project root, mirroring the production project's feature/domain structure. Cross-cutting helpers go under `Support/Shared/` per the principle in [testing#INST0020].
- [INST0012] **Don't** add a `Shared/` folder to a dedicated `<ProductionProject>.Tests.Support` project — the project root is already the shared root, so a `Shared/` bucket is tautological. Organize its types by the production feature/domain structure they mirror ([#INST0010]) or, when a type doesn't map to one production area, by a meaningful logical concern at the project root (e.g., `Async/`, `Encodings/`). The `Support/Shared/` subfolder from [#INST0011] applies only to a single test project's `Support/` folder, where it separates cross-cutting helpers from the production-mirroring ones living beside them.
