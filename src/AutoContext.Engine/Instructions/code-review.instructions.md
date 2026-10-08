---
name: "code-review (v1.0.0)"
description: "Apply when reviewing code, auditing a diff, or giving feedback on a pull request (correctness, security, quality)."
---

# Code Review Instructions

## MCP Tool Validation

When reviewing changes, ground every finding in tool output where one
exists:
- For each touched `.cs` file, call `analyze_csharp_code_style` and
  `analyze_csharp_project_structure` (plus `analyze_csharp_testing_style`
  for test files).
- For each touched `.ts`/`.tsx`/`.js`/`.jsx` file, call `analyze_typescript_code_style`.
- For each touched `.csproj`, call `analyze_nuget_references`.
- For any drafted commit message, call `analyze_git_commit_message`.
Treat any tool-reported violation as a blocking review finding.

## Rules

### Output Structure

- [INST0001] **Do** cite the specific line or block and explain *why* it is a problem — not just *what* is wrong.
- [INST0002] **Do** suggest a concrete fix or direction for each finding.
- [INST0003] **Do** classify each finding as **blocking** (must fix), **suggestion** (should consider), or **nit** (optional polish) — surface blocking issues first.
- [INST0004] **Don't** rewrite the author's approach because you would have done it differently — only flag concrete problems.
- [INST0005] **Don't** repeat the same finding on every occurrence — identify the pattern once and note how many times it appears.

### What to Check

- [INST0006] **Do** check for **correctness** — logic errors, off-by-one, null dereferences, race conditions, incorrect state transitions.
- [INST0007] **Do** check for **security** — injection vectors, broken auth, sensitive data exposure, insecure defaults.
- [INST0008] **Do** check **error handling** — unhandled exceptions, silent failures, missing cancellation, resource leaks.
- [INST0009] **Do** check **code quality** — dead code, duplicated logic, unnecessary complexity, missed simplification or existing utility reuse.
- [INST0010] **Do** check for **unintended changes** — accidentally staged files, removed lines that shouldn't have been deleted, debug leftovers.
- [INST0011] **Do** check **consistency** — does the change follow the patterns already established in the surrounding code and project?
- [INST0012] **Do** check **tests** — are new behaviors covered? are edge cases and failure paths tested?
- [INST0013] **Do** check **API surface** — are new public members necessary? are names clear to callers who lack context?
- [INST0014] **Do** check for **side effects** — does the change affect shared state, configuration, or infrastructure in ways not obvious from the diff?
