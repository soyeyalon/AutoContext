---
name: "dotnet-mstest (v1.0.0)"
description: "Apply when writing or reviewing MSTest tests, test initialization, or MSTest-specific APIs in .NET."
applyTo: "**/*Tests*.{cs,fs,vb,razor}"
---

# MSTest Instructions

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

- [INST0001] **Do** use **MSTest v4** with the `[TestClass]` / `[TestMethod]` attribute model.
- [INST0002] **Do** use `[TestMethod]` + `[DataRow]` for parameterised tests instead of duplicating test methods.
- [INST0003] **Do** use `Assert.ThrowsExactly<T>()` / `await Assert.ThrowsExactlyAsync<T>()` to verify expected exceptions.
- [INST0004] **Do** use `Assert.That` for expression-based assertions — it inspects the expression result and produces clear failure messages.
- [INST0005] **Do** use `[TestInitialize]` and `[TestCleanup]` for per-test setup/teardown; use `[ClassInitialize]` and `[ClassCleanup]` only for expensive shared resources.
- [INST0006] **Do** use `[AssemblyInitialize]` and `[AssemblyCleanup]` sparingly — only for truly global setup like test database creation.
- [INST0007] **Do** use `TestContext` to access test metadata and output diagnostics — be aware that test-run properties are not available during `[AssemblyInitialize]` or `[ClassInitialize]`.
- [INST0008] **Do** use `[Timeout]` at method level (not class level) to guard against hangs in async tests.
- [INST0009] **Do** use string interpolation in assertion messages — the `message` + `object[]` overloads were removed in v4.
- [INST0010] **Don't** use `[ExpectedException]` — it was removed in v4; use `Assert.ThrowsExactly<T>` instead.
- [INST0011] **Don't** share mutable state between tests via static fields — MSTest does not guarantee execution order.
