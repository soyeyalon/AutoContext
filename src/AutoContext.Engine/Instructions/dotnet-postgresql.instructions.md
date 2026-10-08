---
name: "dotnet-postgresql (v1.0.0)"
description: "Apply when writing or reviewing PostgreSQL queries, PL/pgSQL functions, migrations, or data access in .NET projects."
applyTo: "**/*.{cs,fs,vb}"
---

# PostgreSQL Instructions

> These instructions target PostgreSQL used in .NET projects with Npgsql or Npgsql.EntityFrameworkCore.PostgreSQL.

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

### Security

- [INST0001] **Do** use parameterized queries with Npgsql `@param` syntax for all dynamic SQL — never concatenate user input into query strings; SQL injection is the most common critical vulnerability in database-backed applications.
- [INST0002] **Do** restrict the database user's `search_path` to fixed schemas — a misconfigured or attacker-controlled `search_path` can redirect function and table lookups to a malicious schema, enabling privilege escalation.
- [INST0003] **Do** enable Row-Level Security (RLS) on tables that contain multi-tenant data — RLS enforces access control at the database layer, providing defence-in-depth even if the application layer is compromised.
- [INST0004] **Don't** store secrets (passwords, connection strings, API keys) in PostgreSQL objects — use application-level secrets management; secrets in stored functions or comments are readable by any database user with sufficient privilege.

### Query Design

- [INST0005] **Do** use the `RETURNING` clause after `INSERT`, `UPDATE`, and `DELETE` to retrieve affected rows in a single round-trip — avoids a separate `SELECT` and is safe under concurrent access.
- [INST0006] **Do** use `INSERT … ON CONFLICT DO UPDATE` (upsert) rather than a separate `SELECT` + `INSERT` — the latter is not atomic and fails under concurrent load; `ON CONFLICT` handles the race condition at the database level.
- [INST0007] **Do** use CTEs (`WITH … AS (…)`) for readability; be explicit about materialisation — PostgreSQL 12+ inlines single-reference non-recursive CTEs by default, so add `MATERIALIZED` when you need a plan-fence and `NOT MATERIALIZED` when you want forced inlining.
- [INST0008] **Do** use `LATERAL` joins to reference columns from preceding `FROM` items inside a subquery — `LATERAL` enables per-row subqueries and is the PostgreSQL equivalent of `CROSS APPLY` in T-SQL.
- [INST0009] **Do** use `jsonb` operators (`->`, `->>`, `@>`, `?`) for querying JSON columns — `jsonb` is indexed, binary-stored, and faster to query; prefer it over `json` except when preserving key order or duplicate keys is required.
- [INST0010] **Do** use `ILIKE` for case-insensitive pattern matching — `ILIKE` is native to PostgreSQL and is clearer than `LOWER(col) LIKE LOWER(pattern)`; pair it with a `pg_trgm` GIN index for performance on large tables.
- [INST0011] **Don't** use `NOT IN` with a subquery that can return `NULL` — if any row in the subquery is `NULL`, the entire `NOT IN` predicate evaluates to `UNKNOWN` and returns no rows; prefer `NOT EXISTS` instead.
- [INST0012] **Don't** use `SELECT *` in application queries — list columns explicitly; `SELECT *` breaks when columns are added or reordered and prevents covering-index-only scans.

### Performance

- [INST0013] **Do** run `EXPLAIN ANALYZE` on slow queries to understand the actual execution plan — `EXPLAIN` alone shows the estimated plan; `ANALYZE` executes the query and shows actual row counts and timing, revealing plan misestimates.
- [INST0014] **Do** create partial indexes for queries that filter on a fixed condition (e.g., `WHERE is_active = true`) — partial indexes are smaller, faster to scan, and cheaper to maintain than full-table indexes.
- [INST0015] **Do** create indexes `CONCURRENTLY` in production environments — `CREATE INDEX` without `CONCURRENTLY` acquires an `AccessExclusiveLock` that blocks all reads and writes; `CONCURRENTLY` builds the index without blocking at the cost of a longer build time.
- [INST0016] **Do** use `COPY` (or Npgsql's `BeginBinaryImport`) for bulk inserts — `COPY` bypasses per-row overhead and is orders of magnitude faster than individual `INSERT` statements for large data loads.
- [INST0017] **Don't** apply functions to indexed columns in `WHERE` clauses (e.g., `WHERE LOWER(email) = …`) unless a matching functional index exists — function calls prevent index seeks and force sequential scans; create a functional index or use a `GENERATED ALWAYS AS` generated column instead.
- [INST0018] **Don't** ignore table bloat in high-churn tables — dead tuples from `UPDATE` and `DELETE` accumulate and degrade query performance; `autovacuum` handles most cases, but if you notice progressive slowdowns, check `pg_stat_user_tables.n_dead_tup` and consider tuning autovacuum thresholds with the DBA.

### Functions & Procedures

- [INST0019] **Do** use `$$ … $$` dollar-quoting for PL/pgSQL function bodies — dollar-quoting avoids escaping issues with single quotes inside the body and is the standard PostgreSQL convention.
- [INST0020] **Do** use `RETURNS TABLE (…)` for set-returning functions instead of `SETOF record` — typed return columns make the function self-documenting and allow callers to reference columns by name without a column definition list.
- [INST0021] **Do** handle expected errors in PL/pgSQL with an `EXCEPTION` block and use `RAISE EXCEPTION` with an `ERRCODE` to surface application-level errors — `RAISE` with a specific error code allows callers to catch and handle distinct error conditions.

### Schema & Migrations

- [INST0022] **Do** use `timestamptz` (`TIMESTAMP WITH TIME ZONE`) for all date-time columns — `timestamp without time zone` stores and returns values as given with no conversion; `timestamptz` stores UTC and converts to the session time zone, preventing off-by-one-hour bugs across DST boundaries.
- [INST0023] **Do** use `gen_random_uuid()` (built-in since PostgreSQL 13) for UUID primary keys — avoids a dependency on the `uuid-ossp` extension; pair with a `uuid` column type for type safety.
- [INST0024] **Do** always include `IF NOT EXISTS` / `IF EXISTS` guards in migration scripts — migrations must be idempotent so they can be re-run safely without duplicate-object errors.
- [INST0025] **Do** prefer `text` over `varchar(n)` unless you need to enforce a maximum length at the database level — in PostgreSQL, `text` and `varchar` have identical storage and performance; `varchar(n)` adds only a length-check constraint with no other benefit.
- [INST0026] **Don't** use `SERIAL` or `BIGSERIAL` in new tables — use `GENERATED ALWAYS AS IDENTITY` (SQL standard, PostgreSQL 10+) instead; it is cleaner, prevents accidental manual inserts into the sequence column, and integrates better with ORMs.
