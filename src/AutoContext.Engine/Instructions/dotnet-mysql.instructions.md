---
name: "dotnet-mysql (v1.0.0)"
description: "Apply when writing or reviewing MySQL queries, stored procedures, migrations, or data access in .NET projects."
applyTo: "**/*.{cs,fs,vb}"
---

# MySQL Instructions

> These instructions target MySQL used in .NET projects with MySqlConnector or Pomelo.EntityFrameworkCore.MySql.

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

- [INST0001] **Do** use parameterized queries with MySqlConnector `@param` syntax for all dynamic SQL — never concatenate user input into query strings; SQL injection is the primary critical vulnerability in database-backed applications.
- [INST0002] **Do** grant the minimum privileges needed — prefer granting `EXECUTE` on stored procedures over direct `SELECT`/`INSERT`/`UPDATE`/`DELETE` on tables; never run the application as `root`.
- [INST0003] **Do** use TLS for all connections (`SslMode=Required` or `SslMode=VerifyCA` in the connection string) — MySQL traffic is unencrypted by default; without TLS, credentials and data travel in plaintext.
- [INST0004] **Don't** store secrets (passwords, API keys, connection strings) in stored procedures, views, or comments — use application-level secrets management; `SHOW CREATE PROCEDURE` exposes the full body to any user with `SHOW` privilege.

### Query Design

- [INST0005] **Do** specify only the columns you need instead of `SELECT *` — `SELECT *` breaks code when schema changes, prevents covering-index-only scans, and wastes bandwidth.
- [INST0006] **Do** use `INSERT … ON DUPLICATE KEY UPDATE` for upserts rather than a separate `SELECT` + `INSERT` — the latter is not atomic and fails under concurrent load; `ON DUPLICATE KEY UPDATE` handles the race condition at the engine level.
- [INST0007] **Do** use `INSERT IGNORE` or `REPLACE` only when you genuinely want to discard duplicates or overwrite the row — both silently suppress errors; prefer `ON DUPLICATE KEY UPDATE` when you need to update specific columns.
- [INST0008] **Do** use `EXISTS` instead of `COUNT(*) > 0` to check for row existence — `EXISTS` short-circuits on the first matching row; `COUNT` scans all matching rows.
- [INST0009] **Do** handle `NULL` explicitly — use `IS NULL` / `IS NOT NULL`; comparisons using `=` or `<>` against `NULL` return `NULL`, not `TRUE` or `FALSE`, and `NULL` propagates through arithmetic and string operations.
- [INST0010] **Don't** use `NOT IN` with a subquery that can return `NULL` — if any row in the subquery is `NULL`, the entire `NOT IN` predicate evaluates to `NULL` and returns no rows; prefer `NOT EXISTS` instead.
- [INST0011] **Don't** rely on implicit character set conversions — always specify `CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci` (or a project-wide default) on tables and columns to avoid mojibake and broken comparisons.

### Performance

- [INST0012] **Do** add indexes to columns used in `WHERE`, `JOIN`, `ORDER BY`, and `GROUP BY` clauses — missing indexes are the most common cause of full table scans.
- [INST0013] **Do** prefix slow queries with `EXPLAIN ANALYZE` (MySQL 8.0.18+) to see actual execution times and row counts — `EXPLAIN` alone shows estimates; `ANALYZE` runs the query and reveals where estimates diverge from reality.
- [INST0014] **Do** use `LOAD DATA INFILE` (or MySqlConnector's `MySqlBulkLoader`) for bulk inserts — `LOAD DATA` bypasses per-row overhead and is orders of magnitude faster than individual `INSERT` statements.
- [INST0015] **Do** prefer `InnoDB` for all tables — InnoDB supports transactions, row-level locking, and foreign keys; `MyISAM` uses table-level locking and has no crash recovery.
- [INST0016] **Don't** apply functions to indexed columns in `WHERE` clauses (e.g., `WHERE DATE(created_at) = …`) unless a matching functional index exists (MySQL 8.0.13+) — function calls prevent index lookups and force full table scans.
- [INST0017] **Don't** use `FORCE INDEX` or `USE INDEX` hints as a permanent fix for slow queries — hints break when the optimizer or data distribution changes; fix the root cause with proper indexing or query restructuring instead.

### Stored Procedures & Functions

- [INST0018] **Do** use `DELIMITER` when creating stored procedures in script files — MySQL uses `;` as the default statement delimiter; without `DELIMITER`, the procedure body is parsed as separate statements and the `CREATE` fails.
- [INST0019] **Do** use `DECLARE … HANDLER FOR SQLEXCEPTION` to handle expected errors inside stored procedures — unhandled errors abort the procedure silently; a handler lets you log, roll back, and re-signal with a meaningful message.
- [INST0020] **Do** use explicit transactions (`START TRANSACTION … COMMIT / ROLLBACK`) for multi-statement mutations — never rely on `autocommit` for operations that must be atomic.
- [INST0021] **Don't** use `SELECT … INTO` to assign variables when the query might return zero rows — the variable retains its previous value; use a `DECLARE CONTINUE HANDLER FOR NOT FOUND` to detect the empty-result case.

### Schema & Migrations

- [INST0022] **Do** use `DATETIME(6)` or `TIMESTAMP(6)` for date-time columns that need sub-second precision — MySQL `DATETIME` defaults to whole-second precision; fractional digits are silently truncated without the `(fsp)` suffix.
- [INST0023] **Do** store all timestamps in UTC and convert at the application layer — mixing time zones in MySQL `TIMESTAMP` columns causes confusion because MySQL converts them to/from the session `time_zone` setting on read and write.
- [INST0024] **Do** always include `IF NOT EXISTS` / `IF EXISTS` guards in migration scripts — migrations must be idempotent so they can be re-run safely without duplicate-object errors.
- [INST0025] **Do** name constraints explicitly (e.g., `CONSTRAINT pk_users PRIMARY KEY`, `CONSTRAINT uq_users_email UNIQUE`) — auto-generated names differ across environments and make rollback scripts harder to write.
- [INST0026] **Don't** use `FLOAT` or `DOUBLE` for monetary or precision-critical values — use `DECIMAL(p,s)`; floating-point types introduce rounding errors that accumulate silently.
