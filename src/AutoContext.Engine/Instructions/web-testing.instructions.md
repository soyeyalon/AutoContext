---
name: "web-testing (v1.0.0)"
description: "Apply when writing or reviewing JavaScript or TypeScript tests, test structure, or test organization."
applyTo: "**/*.{test,spec,cy}.{js,jsx,ts,tsx,mjs,mts}"
---

# Web Testing Instructions

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content`. Treat any reported
violation as blocking — fix it before reporting the work as done.

## Rules

### General

- [INST0001] **Do** nest `describe` blocks — outer `describe` per class or module, inner `describe` per method or behavior.
- [INST0002] **Do** prefix test names with `should` or `should not` (e.g. `"should throw when value is null"`, `"should not throw when value is valid"`).
- [INST0003] **Do** use `beforeEach` / `afterEach` for per-test setup and teardown — reserve suite-level hooks for expensive shared resources.
- [INST0004] **Do** prefer `async/await` over `done` callbacks for async tests — callback-style is less readable and error-prone on timeout.
- [INST0005] **Don't** leave focus or skip markers (`.only`, `.skip`, `fdescribe`, `fit`, `xit`) in committed tests — they silently reduce coverage.

### Assertions

- [INST0006] **Do** use `expect(…).toBe(…)` for primitives or identity checks and `expect(…).toEqual(…)` for deep object comparisons.
- [INST0007] **Do** prefer `toHaveBeenCalledWith(…)` over inspecting raw mock `calls` arrays.
- [INST0008] **Do** use optional chaining (`?.`, `[i]?.`) instead of non-null assertions (`!.`) when accessing values after a soft/grouped guard assertion — a soft `toBeDefined()` or `toHaveLength()` that fails still continues execution, so subsequent property access must not throw a `TypeError`.

### Test Support

- [INST0009] **Do** place tests under a `tests/` folder at the project root that mirrors the production source structure, and place reusable test-support code that belongs to a single project inside `tests/support/` within it (e.g. `<project-root>/tests/support/<feature>/<helper>.ts`). Cross-cutting helpers go under `tests/support/shared/` per the principle in [testing#INST0020].
- [INST0010] **Do** extract test-support code that's reused across multiple test suites into a dedicated test-support package whose `src/` is the support code, mirroring the production project's feature/domain structure. Cross-cutting helpers go under `src/shared/` per the principle in [testing#INST0020]. Do not nest another `tests/support/` inside such a package — its `src/` already is the test support. Name the package following the host workspace's adopted convention: by default use the npm/JS convention (lowercase, dasherized, e.g. `my-project-tests-support`) and keep the directory name aligned with the `package.json` `name` field. Defer to a workspace-wide naming convention only when one has been adopted (e.g. in a .NET-driven workspace the package directory may use PascalCase with dots — `MyProject.Tests.Support` — to match sibling .NET projects, with the npm `name` field dasherized as required by npm validation).
