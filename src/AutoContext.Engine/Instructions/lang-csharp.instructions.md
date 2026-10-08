---
name: "lang-csharp (v1.0.0)"
description: "Apply when writing or reviewing C# code (member ordering, formatting, language features, nullability, XML docs)."
applyTo: "**/*.cs"
---

# C# Coding Instructions

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

### Naming

- [INST0001] **Do** name private readonly fields with a leading underscore and camelCase (e.g., `_workerQueue`); name private static readonly fields with PascalCase and no prefix (e.g., `DefaultTimeout`).
- [INST0002] **Do** use PascalCase for all constants, both fields and local constants.
- [INST0003] **Do** use language keywords for built-in types instead of BCL type names — `string` not `String`, `int` not `Int32`, `bool` not `Boolean`.

### Member Ordering

- [INST0004] **Do** order members by kind (constants, static fields, fields, constructors, delegates, events, enums, properties, indexers, methods, operators, nested types), then by access level (public → private), then static before instance, then alphabetically.
- [INST0005] **Don't** use `#region` – they hide code structure and make it harder to navigate.

### Language Features

- [INST0006] **Do** use current C# features when they enhance clarity (e.g., file‑scoped namespaces, raw string literals, collection expressions, pattern matching, `record`, `required`, `init`).
- [INST0007] **Do** use `var` when the type is apparent from the right-hand side (`var users = new List<User>()`); use an explicit type when it isn't — prefer clarity over brevity.
- [INST0008] **Do** use `nameof(x)` instead of the string literal `"x"` when referring to a symbol — survives renames and refactors.
- [INST0009] **Do** use pattern-based null checks — `if (x is null)` and `if (x is not null)` — instead of `== null` / `!= null`; pattern syntax is not affected by overloaded equality operators.
- [INST0010] **Do** use the braceless `using` declaration for disposables instead of a `try/finally` block whose only purpose is calling `Dispose` — `using Font font = new(...);` is cleaner and scopes disposal to the enclosing block.
- [INST0011] **Don't** nest conditional expressions (`?:`, `??`) — extract to a method, local variable, or use `if`/`else` for clarity.
- [INST0012] **Don't** write long LINQ chains — break them into intermediate variables or extract into a method when readability suffers.
- [INST0013] **Don't** use `#pragma warning disable` — prefer `[SuppressMessage]` attributes with a justification.

### Formatting & Whitespace

- [INST0014] **Do** group related statements into logical paragraphs separated by blank lines.
- [INST0015] **Do** insert a blank line before control flow statements (`if`, `for`, `foreach`, `while`, `do`, `switch`, `try`, `using`, `lock`).
- [INST0016] **Do** insert a blank line between variable declarations and their usage.
- [INST0017] **Do** place expression-body arrows (`=>`) on the line below the method signature, not at the end of it.
- [INST0018] **Do** always use curly braces for control flow statements, even for single-line bodies — except single-line guard clauses (early `return`/`throw`).

### Nullability

- [INST0019] **Do** keep `#nullable enable` on for every project and treat nullable warnings as errors.
- [INST0020] **Don't** suppress nullable warnings with `!` unless you've proved safety.

### Documentation

- [INST0021] **Do** add XML doc comments (`/// <summary>`) to public and protected types, members, and parameters — keep them brief and focused on intent, not implementation.
- [INST0022] **Don't** use decorative section-header comments (e.g., `// ── Lifecycle ──────`) — organize code through consistent member ordering instead.
