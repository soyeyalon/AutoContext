---
name: "dotnet-razor (v1.0.0)"
description: "Apply when writing or reviewing Razor component files (.razor), directives, code-behind, or Razor markup."
applyTo: "**/*.razor,**/*.razor.cs"
---

# Razor Instructions

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

- [INST0001] **Do** name component files in **PascalCase** matching the component class (e.g., `ProductDetail.razor`); keep one component per `.razor` file.
- [INST0002] **Do** separate components into dedicated files — `.razor` for markup, `.razor.cs` for code-behind, `.razor.css` for scoped styles — avoid `@code` blocks in `.razor` files.
- [INST0003] **Do** order directives at the top of `.razor` files with no blank lines between them, followed by one blank line before markup:
  1. `@page`
  2. `@rendermode`
  3. `@using` — grouped: `System` → `Microsoft` → third-party → app namespaces (alphabetical within each group)
  4. Other directives alphabetically (`@attribute`, `@implements`, `@inject`, `@layout`, `@typeparam`)
- [INST0004] **Do** use `_Imports.razor` for `@using` directives shared across a folder and its subfolders — avoid repeating the same `@using` in every component.
- [INST0005] **Do** use kebab-case route templates matching the component name (e.g., `ProductDetail` → `@page "/product-detail"`).
- [INST0006] **Do** mark required component parameters with `[EditorRequired]` to surface warnings at design and build time.
- [INST0007] **Do** prefer `@bind` with `@bind:after` over manual event wiring for two-way binding that needs post-bind async work.
- [INST0008] **Do** prefer `@bind:get`/`@bind:set` over legacy `value`/`@onchange` pairs for two-way binding — the bind syntax prevents value-synchronization bugs and makes intent explicit.
- [INST0009] **Do** add `@formname` to plain HTML `<form>` elements when using enhanced form handling or static SSR so the framework can route form posts to the correct handler.
- [INST0010] **Don't** use `builder.AddMarkupContent` with user-supplied strings — use `builder.AddContent` (text-safe) to prevent XSS.
- [INST0011] **Don't** nest Razor control-flow blocks (`@if`, `@foreach`) more than two levels deep — extract a child component or `RenderFragment` instead.
- [INST0012] **Don't** write user-supplied data to the DOM via `innerHTML` or raw markup injection — Razor's default text rendering is XSS-safe; bypass it only with trusted content.
