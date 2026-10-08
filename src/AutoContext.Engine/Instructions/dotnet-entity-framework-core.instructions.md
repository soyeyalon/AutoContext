---
name: "dotnet-entity-framework-core (v1.0.0)"
description: "Apply when writing or reviewing EF Core code (DbContext lifetime, querying, change tracking, eager/lazy loading, migrations)."
applyTo: "**/*.{cs,fs,vb}"
---

# Entity Framework Core Instructions

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

### DbContext & Lifetime

- [INST0001] **Do** register `DbContext` as a `Scoped` service via `AddDbContext<TContext>` — `DbContext` is a unit-of-work, is not thread-safe, and must not be shared across requests or threads.
- [INST0002] **Do** use `AddDbContextPool<TContext>` for high-throughput ASP.NET Core apps — pooling reuses `DbContext` instances and avoids the per-request allocation overhead of `AddDbContext`.
- [INST0003] **Do** use `IDbContextFactory<TContext>` in Blazor Server, background services, and any scenario requiring multiple independent units-of-work — factory-created contexts are not managed by the DI container and must be disposed explicitly.
- [INST0004] **Don't** expose `DbContext` or `IQueryable<T>` outside the data access layer — `IQueryable` leaks unexecuted query composition across layer boundaries and couples callers to EF internals; return materialized collections (`IReadOnlyList<T>` or `IReadOnlyCollection<T>`) instead.
- [INST0005] **Don't** register `DbContext` as `Singleton` — `DbContext` is not thread-safe; a singleton instance shared across concurrent requests causes data corruption and race conditions.

### Querying & Loading

- [INST0006] **Do** use `AsNoTracking()` for read-only queries that do not write back to the database — change tracking snapshots entities in memory and adds overhead that only pays off when `SaveChanges` is called.
- [INST0007] **Do** use `Include`/`ThenInclude` for eager loading of related entities — lazy loading fires a separate database roundtrip for every navigation property access, causing the N+1 query problem.
- [INST0008] **Do** project with `Select` to fetch only the columns you need — loading full entity instances when only a subset of properties is used transfers unnecessary data and wastes memory.
- [INST0009] **Do** always use async EF Core APIs: `ToListAsync`, `FirstOrDefaultAsync`, `SaveChangesAsync`, `ExecuteUpdateAsync`, etc. — synchronous calls block thread-pool threads and reduce scalability under load.
- [INST0010] **Do** use `FromSql` (interpolated) instead of `FromSqlRaw` for raw SQL queries — `FromSql` auto-parameterizes interpolated values and prevents SQL injection; `FromSqlRaw` requires manual parameterization and is vulnerable if misused.
- [INST0011] **Do** use `AsSplitQuery()` when eager-loading multiple collection navigations — a single query with multiple `Include` calls causes cartesian explosion (row duplication that grows exponentially), while split queries issue separate roundtrips per collection.
- [INST0012] **Don't** enable lazy loading in production unless profiling shows it is the better trade-off — it silently generates N+1 queries that are invisible in source code and can cause severe performance degradation under load.

### Migrations & Schema

- [INST0013] **Do** apply migrations at deploy time via the CLI or a CI/CD pipeline (`dotnet ef database update`) — calling `MigrateAsync()` at startup causes race conditions in multi-instance deployments and may fail due to insufficient permissions.
- [INST0014] **Do** review every generated migration file before committing — EF may emit destructive changes (dropping columns, tables, or data) that require manual adjustment or a data migration step.
- [INST0015] **Don't** call `Database.EnsureCreatedAsync()` or `Database.EnsureCreated()` outside of testing — both bypass the migrations system and leave the schema unmanaged; use `dotnet ef migrations` for all production schemas.

### Bulk Operations & Performance

- [INST0016] **Do** use `ExecuteUpdateAsync`/`ExecuteDeleteAsync` (EF Core 7+) for bulk updates and deletes — loading entities just to update or delete them wastes memory and generates a separate SQL statement per row.
- [INST0017] **Do** enable connection resiliency (`EnableRetryOnFailure`) when targeting cloud databases (Azure SQL, etc.) — transient network and server failures are expected in cloud environments and the built-in execution strategy retries them automatically.

### Model Configuration

- [INST0018] **Do** use `IEntityTypeConfiguration<T>` to configure entities in separate classes instead of inlining everything in `OnModelCreating` — keeps model configuration modular, testable, and follows SRP.
- [INST0019] **Do** use global query filters (`HasQueryFilter`) for cross-cutting concerns like soft-delete and multi-tenancy — ensures filters are applied consistently without requiring manual `.Where()` on every query.
- [INST0020] **Do** configure a concurrency token (`[Timestamp]` / `IsRowVersion()`) on entities that can be updated concurrently — without one, last-write-wins silently overwrites other users' changes with no conflict detection.
