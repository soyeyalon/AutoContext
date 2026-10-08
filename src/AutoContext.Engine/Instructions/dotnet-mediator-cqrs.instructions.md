---
name: "dotnet-mediator-cqrs (v1.0.0)"
description: "Apply when writing or reviewing CQRS code in .NET (MediatR handlers, pipelines, command/query separation)."
applyTo: "**/*.{cs,fs,vb}"
---

# Mediator / CQRS Instructions

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

### Command & Query Separation

- [INST0001] **Do** separate commands (write) from queries (read) — a single handler should not both mutate state and return projected data.
- [INST0002] **Do** keep commands and queries as simple DTOs (records or classes with public properties) — no behavior or injected services on the request object itself.
- [INST0003] **Do** name requests after intent: `CreateOrderCommand`, `GetOrderByIdQuery` — the name should describe the operation, not the handler.

### Handler Design

- [INST0004] **Do** keep handlers focused on a single operation — if a handler grows beyond orchestration, extract domain logic into a dedicated service.
- [INST0005] **Do** accept `CancellationToken` in every handler and pass it to all async calls downstream.
- [INST0006] **Don't** inject `IMediator` / `ISender` into handlers to dispatch more requests — it creates hidden coupling chains; call services directly or compose in the application layer.
- [INST0007] **Don't** use the mediator as a general-purpose service locator — it is for dispatching requests through a pipeline, not for resolving arbitrary dependencies.

### Pipeline Behaviors

- [INST0008] **Do** use pipeline behaviors for cross-cutting concerns (validation, logging, transaction wrapping) — keep them thin and composable.
- [INST0009] **Do** order behaviors deliberately: validation before execution, transaction wrapping around the handler, logging at the outermost layer.
- [INST0010] **Don't** put business logic in pipeline behaviors — they should enforce policy, not implement features.

### Notifications

- [INST0011] **Do** use notifications for fire-and-forget side effects (audit logging, cache invalidation, event forwarding) — never for operations that the caller depends on.
- [INST0012] **Don't** assume notification handler execution order — handlers run in registration order but should be independent of each other.
