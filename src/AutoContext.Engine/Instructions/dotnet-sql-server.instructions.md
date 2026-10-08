---
name: "dotnet-sql-server (v1.0.0)"
description: "Apply when writing or reviewing T-SQL queries, stored procedures, migrations, or data access in .NET projects."
applyTo: "**/*.{cs,fs,vb}"
---

# T-SQL / SQL Server Instructions

> These instructions target T-SQL as used with SQL Server, Azure SQL Database, and Azure SQL Managed Instance.

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

- [INST0001] **Do** use parameterized queries or `sp_executesql` with parameters for all dynamic SQL — concatenating user input directly into SQL strings is the primary cause of SQL injection vulnerabilities.
- [INST0002] **Do** grant the minimum permissions needed — prefer `EXECUTE` on stored procedures over direct `SELECT`/`INSERT`/`UPDATE`/`DELETE` on tables; never grant `db_owner` to application accounts.
- [INST0003] **Do** use schema-qualified object names (e.g., `dbo.Users`, not just `Users`) — unqualified names force SQL Server to check multiple schemas, can cause ambiguity, and bypass plan caching.
- [INST0004] **Don't** store secrets (passwords, API keys, connection strings) in SQL scripts, stored procedures, or database objects — use application-level secrets management or SQL Server features like Always Encrypted.

### Query Design

- [INST0005] **Do** specify only the columns you need instead of `SELECT *` — `SELECT *` breaks code when schema changes, prevents covering index use, and wastes bandwidth.
- [INST0006] **Do** use set-based operations over row-by-row cursors or `WHILE` loops — set-based queries leverage SQL Server's query optimizer; loops serialize work and scale poorly.
- [INST0007] **Do** use CTEs (`WITH cte AS (…)`) to break complex queries into readable, named steps — prefer CTEs over deeply nested subqueries for readability; use temp tables or `#table` when the intermediate result set is large and reused multiple times.
- [INST0008] **Do** filter with `WHERE`, `JOIN … ON`, and `HAVING` on indexed columns — push filters as early as possible to reduce rows processed; avoid applying functions to indexed columns in `WHERE` clauses as this prevents index seeks.
- [INST0009] **Do** handle `NULL` explicitly — use `IS NULL` / `IS NOT NULL`; comparisons using `=` or `<>` against `NULL` always return `UNKNOWN`, not `TRUE` or `FALSE`, and `NULL` propagates through arithmetic and string operations.
- [INST0010] **Don't** use `SELECT DISTINCT` as a workaround for duplicate rows caused by a bad join — fix the join instead; distinct forces a sort or hash operation over the entire result set.
- [INST0011] **Don't** use `NOT IN` with a subquery that can return `NULL` — if any row in the subquery is `NULL`, the entire `NOT IN` predicate evaluates to `UNKNOWN` and returns no rows; prefer `NOT EXISTS` instead.

### Performance

- [INST0012] **Do** add indexes to columns used in `WHERE`, `JOIN`, `ORDER BY`, and `GROUP BY` clauses — missing indexes are the most common cause of table scans and slow queries.
- [INST0013] **Do** use `EXISTS` instead of `COUNT(*) > 0` to check for row existence — `EXISTS` short-circuits on the first matching row; `COUNT` scans all matching rows.
- [INST0014] **Do** use `TOP (n)` with `ORDER BY` to return a limited, deterministic result — without `ORDER BY`, `TOP` returns an arbitrary subset.
- [INST0015] **Do** set `SET NOCOUNT ON` at the top of stored procedures and triggers — suppresses the "n rows affected" message, reducing network traffic and avoiding interference with `@@ROWCOUNT` in calling code.
- [INST0016] **Don't** use `WITH (NOLOCK)` / `READ UNCOMMITTED` to cure blocking — it causes dirty reads, phantom reads, and rows that silently disappear or appear twice; address root-cause blocking with proper indexing and shorter transactions instead.
- [INST0017] **Don't** call scalar user-defined functions (UDFs) in `WHERE` clauses or on large result sets — scalar UDFs execute once per row, inhibit parallelism, and are a common source of performance regressions; prefer inline table-valued functions or computed columns.

### Stored Procedures & Batches

- [INST0018] **Do** wrap multi-statement stored procedures in `BEGIN TRY … END TRY BEGIN CATCH … END CATCH` — unhandled errors in T-SQL silently continue to the next statement; `TRY/CATCH` lets you log errors, roll back transactions, and re-throw with `THROW`.
- [INST0019] **Do** use explicit transactions (`BEGIN TRANSACTION … COMMIT / ROLLBACK`) for any operation that must be atomic — never rely on implicit transaction behaviour for multi-step mutations.
- [INST0020] **Do** check `@@TRANCOUNT` before committing or rolling back in nested transaction scenarios — committing an inner `BEGIN TRANSACTION` does not commit to disk; only the outermost `COMMIT` does.
- [INST0021] **Do** use `OUTPUT` clause to return inserted, updated, or deleted rows in a single round-trip — avoids a separate `SELECT` after a DML statement and is safe under concurrent access.
- [INST0022] **Don't** use `RAISERROR` in new code — use `THROW` instead; `THROW` re-throws the original error number and is consistent with `TRY/CATCH` semantics.

### Schema & Migrations

- [INST0023] **Do** always include `IF NOT EXISTS` / `IF EXISTS` guards in migration scripts — migrations must be idempotent so they can be re-run safely without duplicate-object errors.
- [INST0024] **Do** use `ALTER TABLE … ADD column … NULL` when adding columns to existing tables — adding a `NOT NULL` column without a default to a populated table requires a full table rewrite in older SQL Server versions and acquires a schema lock.
- [INST0025] **Do** name constraints explicitly (e.g., `CONSTRAINT PK_Users PRIMARY KEY`, `CONSTRAINT UQ_Users_Email UNIQUE`) — auto-generated constraint names differ across environments and make migrations and rollbacks harder to script.
