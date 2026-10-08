---
name: "web-playwright (v1.0.0)"
description: "Apply when writing or reviewing Playwright end-to-end tests, page objects, or Playwright-specific APIs."
applyTo: "**/*.{test,spec}.{js,jsx,ts,tsx,mjs,mts}"
---

# Playwright Instructions

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content`. Treat any reported
violation as blocking — fix it before reporting the work as done.

## Rules

- [INST0001] **Do** use `test` / `expect` from `@playwright/test` — avoid mixing with other assertion libraries.
- [INST0002] **Do** use auto-waiting locators (`page.getByRole`, `page.getByText`, `page.getByLabel`, `page.getByTestId`) instead of CSS/XPath selectors.
- [INST0003] **Do** prefer `getByRole` for interactive elements — it aligns tests with accessible semantics.
- [INST0004] **Do** use `expect(locator).toBeVisible()`, `toHaveText()`, `toHaveValue()` and other web-first assertions that automatically retry.
- [INST0005] **Do** use the Page Object Model to abstract page interactions — keep locators and actions in page classes, assertions in tests.
- [INST0006] **Do** use `test.beforeEach` for navigation and common setup; use fixtures for reusable state (auth, seeded data).
- [INST0007] **Do** use `--project` to run against multiple browsers in CI; test at least Chromium and Firefox.
- [INST0008] **Do** use `page.waitForResponse` or `page.route` when tests depend on network calls — avoid arbitrary `page.waitForTimeout`.
- [INST0009] **Don't** use `page.waitForTimeout` for flaky waits — always wait on a specific condition, locator, or network event.
- [INST0010] **Don't** share page state between tests — each test gets a fresh `BrowserContext` by default; preserve that isolation.
- [INST0011] **Don't** hardcode URLs — use `baseURL` in `playwright.config` and navigate with relative paths.
