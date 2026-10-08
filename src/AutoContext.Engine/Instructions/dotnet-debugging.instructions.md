---
name: "dotnet-debugging (v1.0.0)"
description: "Apply when debugging issues, troubleshooting failures, reproducing bugs, or writing regression tests in .NET."
applyTo: "**/*.{cs,fs,vb}"
---

# Debugging & Troubleshooting Instructions

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

- [INST0001] **Do** reproduce bugs in isolation—create a minimal repro case or failing test before diving in.
- [INST0002] **Do** write a failing unit or integration test for any bug you fix; that test then becomes part of your regression suite.
- [INST0003] **Do** log at appropriate levels (Debug / Verbose) and include correlation IDs to trace multi-component flows.
- [INST0004] **Don't** leave temporary debug helpers in committed code (e.g., `Debugger.Break()`, commented-out code).
- [INST0005] **Don't** guess at fixes—verify the hypothesis by reading the relevant code and tracing the logic.
