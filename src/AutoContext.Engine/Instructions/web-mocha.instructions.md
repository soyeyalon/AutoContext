---
name: "web-mocha (v1.0.0)"
description: "Apply when writing or reviewing Mocha tests or Mocha-specific configuration."
applyTo: "**/*.{test,spec}.{js,jsx,ts,tsx,mjs,mts}"
---

# Mocha Instructions

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content`. Treat any reported
violation as blocking — fix it before reporting the work as done.

## Rules

- [INST0001] **Do** pair Mocha with a dedicated assertion library (Chai, `node:assert`, or `node:assert/strict`) — Mocha provides no built-in assertions.
- [INST0002] **Do** return a Promise or use `async/await` for async tests — never mix callbacks (`done`) with Promises.
- [INST0003] **Do** use Sinon.js (`sinon.stub`, `sinon.spy`, `sinon.fake`) for mocking when needed — always call `sinon.restore()` in `afterEach`.
- [INST0004] **Do** set a reasonable `--timeout` globally (for integration tests) and prefer the default 2 000 ms for unit tests.
- [INST0005] **Do** use `--exit` flag in CI to force Mocha to exit after tests complete when lingering handles are unavoidable.
- [INST0006] **Don't** use arrow functions (`() =>`) for `describe` / `it` / `before*` / `after*` when you need access to Mocha's `this` context (e.g., `this.timeout()`).
