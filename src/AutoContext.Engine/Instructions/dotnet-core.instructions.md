---
name: "dotnet-core (v1.0.0)"
description: "Apply when writing or reviewing .NET dependency injection, logging, configuration, or security setup."
applyTo: "**/*.{cs,fs,vb}"
---

# .NET Core Instructions

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

### Dependency Injection

- [INST0001] **Do** use constructor injection — avoid pulling services from `IServiceProvider` directly outside of factories.
- [INST0002] **Do** organize DI registrations into feature-scoped `ServiceCollectionExtensions` classes — one per feature folder, not one big file in `Program.cs`.
- [INST0003] **Do** default to `Scoped` for business services (per-request state) and `Singleton` for stateless infrastructure (evaluators, registries, config facades).
- [INST0004] **Do** register against interfaces, not concrete types — keep consumers decoupled from implementations.
- [INST0005] **Do** use `TryAdd*` / `TryAddEnumerable` in reusable library code to avoid duplicate registrations when modules are composed.
- [INST0006] **Don't** inject `IServiceProvider` into business logic — it hides dependencies and makes testing harder.
- [INST0007] **Don't** capture a `Scoped` service inside a `Singleton` — it causes lifetime mismatch bugs.

### Logging

- [INST0008] **Do** inject `ILogger<T>` via constructor — never use `LoggerFactory.Create` or static loggers.
- [INST0009] **Do** use `[LoggerMessage]` source-generated partial methods for structured logging — they are higher performance, AOT-friendly, and avoid boxing allocations compared to the `ILogger` extension methods.
- [INST0010] **Do** choose log levels deliberately: `Debug` for dev diagnostics, `Information` for business events, `Warning` for recoverable issues, `Error` for failures.
- [INST0011] **Do** use `logger.BeginScope(…)` to attach ambient context (e.g., request ID, user ID) across a multi-step operation.
- [INST0012] **Don't** log sensitive data (passwords, tokens, PII) — scrub or omit before logging.

### Configuration

- [INST0013] **Do** bind configuration to strongly-typed options classes with `services.Configure<T>(…)` — don't scatter `IConfiguration["key"]` reads through business logic.
- [INST0014] **Do** use `IOptions<T>` for singleton lifetime, `IOptionsSnapshot<T>` for scoped (per-request reload), `IOptionsMonitor<T>` for singleton with live reload.
- [INST0015] **Do** validate options at startup with `ValidateDataAnnotations()` and `ValidateOnStart()` to catch missing or invalid config early.
- [INST0016] **Don't** store secrets in `appsettings.json` — use environment variables, .NET Secret Manager (dev), or Azure Key Vault (production).

### Security

- [INST0017] **Do** use `AddAuthentication` / `AddAuthorization` — never implement custom auth middleware from scratch.
- [INST0018] **Do** apply authorization declaratively at the endpoint level using `[Authorize]` or any known project-specific attribute that derives from it; use `[AllowAnonymous]` to explicitly opt out.
- [INST0019] **Do** use `IDataProtectionProvider` to protect sensitive data at rest (e.g., tokens, cookies).
- [INST0020] **Do** use HTTPS redirection middleware (`UseHttpsRedirection`) and HSTS (`UseHsts`) in production.
- [INST0021] **Don't** store secrets or connection strings in source-controlled config files — keep them in environment variables or a secrets store.
