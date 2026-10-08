---
name: "web-vitest (v1.0.0)"
description: "Apply when writing or reviewing Vitest tests, mocks, or Vitest-specific APIs."
applyTo: "**/*.{test,spec}.{js,jsx,ts,tsx,mjs,mts}"
---

# Vitest Instructions

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content`. Treat any reported
violation as blocking — fix it before reporting the work as done.

## Rules

- [INST0001] **Do** use `test` over `it` for test blocks — but if the existing codebase already uses one consistently, follow the project's convention.
- [INST0002] **Do** use tagged template syntax for `test.each` with a human-readable label column for test name interpolation.
- [INST0003] **Do** use `vi.fn()` for spy/stub creation and `vi.mock('module')` for module-level mocking.
- [INST0004] **Do** use `vi.spyOn(object, 'method')` when you need to observe calls without replacing the implementation.
- [INST0005] **Do** use inline snapshots (`toMatchInlineSnapshot()`) for small values; file snapshots for larger structures.
- [INST0006] **Do** use `vi.useFakeTimers()` and `vi.advanceTimersByTime()` when testing timer-dependent code — call `vi.useRealTimers()` in `afterEach`.
- [INST0007] **Do** pass the error class to `.toThrow()` — not a string message.
- [INST0008] **Don't** use `vi.mock` inside `beforeEach` — hoist it to the top level of the file so Vitest can transform it at compile time.
