---
name: "dotnet-nunit (v1.0.0)"
description: "Apply when writing or reviewing NUnit tests, assertions, or NUnit-specific APIs in .NET."
applyTo: "**/*Tests*.{cs,fs,vb,razor}"
---

# NUnit Instructions

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

- [INST0001] **Do** use **NUnit 4** with the constraint-based assertion model (`Assert.That(…, Is.EqualTo(…))`).
- [INST0002] **Do** use `[TestCase]` for inline parameterised tests and `[TestCaseSource]` for complex or shared data.
- [INST0003] **Do** use `Assert.ThrowsAsync<T>(async () => …)` or `Assert.That(async () => …, Throws.TypeOf<T>())` to verify async exceptions.
- [INST0004] **Do** use `Assert.Multiple(() => { … })` so all assertions execute and all failures are reported together.
- [INST0005] **Do** use `[SetUp]` / `[TearDown]` for per-test lifecycle and `[OneTimeSetUp]` / `[OneTimeTearDown]` for expensive shared resources.
- [INST0006] **Do** use `TestContext.Out` or `TestContext.WriteLine` for test diagnostics output.
- [INST0007] **Do** use string interpolation in assertion messages — `Assert.That` overloads with format specification and `params` were removed in NUnit 4.
- [INST0008] **Do** use `[Retry(n)]` only for inherently flaky integration tests — never for unit tests.
- [INST0009] **Do** use `[Category("…")]` to tag slow or integration tests so they can be filtered in CI.
- [INST0010] **Don't** use the classic assertion model (`Assert.AreEqual`) — it was moved to `NUnit.Framework.Legacy.ClassicAssert` in NUnit 4; always prefer the constraint model (`Assert.That`).
- [INST0011] **Don't** use `[ExpectedException]` — it was removed in NUnit 3+; use `Assert.That(…, Throws.TypeOf<T>())` instead.
- [INST0012] **Don't** share mutable state between tests via static fields — NUnit does not guarantee execution order across fixtures.
