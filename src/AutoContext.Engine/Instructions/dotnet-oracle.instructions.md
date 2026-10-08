---
name: "dotnet-oracle (v1.0.0)"
description: "Apply when writing or reviewing Oracle SQL, PL/SQL, stored procedures, migrations, or data access in .NET projects."
applyTo: "**/*.{cs,fs,vb}"
---

# Oracle Instructions

> These instructions target Oracle Database used in .NET projects with Oracle.ManagedDataAccess.Core or Oracle.EntityFrameworkCore.

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

- [INST0001] **Do** use bind variables (`:param` syntax) for all dynamic SQL — never concatenate user input into query strings; SQL injection is the primary critical vulnerability in database-backed applications.
- [INST0002] **Do** grant the minimum privileges needed — prefer `EXECUTE` on PL/SQL packages over direct `SELECT`/`INSERT`/`UPDATE`/`DELETE` on tables; never run the application as `SYS` or `SYSTEM`.
- [INST0003] **Do** use Oracle wallet or external password stores for credentials instead of embedding passwords in connection strings — plaintext passwords in configuration files are a common audit finding.
- [INST0004] **Don't** store secrets (passwords, API keys, tokens) in PL/SQL packages, views, or comments — use application-level secrets management; `DBA_SOURCE` exposes all stored PL/SQL source to privileged users.

### Query Design

- [INST0005] **Do** specify only the columns you need instead of `SELECT *` — `SELECT *` breaks code when schema changes, prevents index-only scans, and wastes bandwidth.
- [INST0006] **Do** use `MERGE … WHEN MATCHED THEN UPDATE WHEN NOT MATCHED THEN INSERT` for upserts — `MERGE` handles the race condition atomically; a separate `SELECT` + `INSERT` is not atomic and fails under concurrent load.
- [INST0007] **Do** use `RETURNING … INTO` on `INSERT`, `UPDATE`, and `DELETE` to retrieve affected values in a single round-trip — avoids a separate `SELECT` and is safe under concurrent access.
- [INST0008] **Do** use `EXISTS` instead of `COUNT(*) > 0` to check for row existence — `EXISTS` short-circuits on the first matching row; `COUNT` must scan all matching rows.
- [INST0009] **Do** use analytic functions (`ROW_NUMBER`, `RANK`, `LAG`, `LEAD`, `SUM … OVER`) instead of self-joins or correlated subqueries for running totals, rankings, and window comparisons — analytic functions process data in a single pass over the result set.
- [INST0010] **Do** use `FETCH FIRST n ROWS ONLY` (Oracle 12c+) for row limiting — it is SQL standard, clearer than wrapping in a subquery with `ROWNUM`, and supports `OFFSET … FETCH` pagination.
- [INST0011] **Don't** use `NOT IN` with a subquery that can return `NULL` — if any row in the subquery is `NULL`, the entire `NOT IN` predicate evaluates to `NULL` and returns no rows; prefer `NOT EXISTS` instead.

### Performance

- [INST0012] **Do** use `EXPLAIN PLAN FOR` and `DBMS_XPLAN.DISPLAY_CURSOR` to understand actual execution plans — estimated plans can diverge from runtime behaviour; always check actual row counts after execution.
- [INST0013] **Do** add indexes to columns used in `WHERE`, `JOIN`, `ORDER BY`, and `GROUP BY` clauses — missing indexes are the most common cause of full table scans.
- [INST0014] **Do** use Oracle bulk operations (`FORALL`, `BULK COLLECT`) in PL/SQL for batch DML — row-by-row processing incurs a context switch between the SQL and PL/SQL engines for each row; bulk operations reduce this to a single switch.
- [INST0015] **Do** use `OracleBulkCopy` (Oracle.ManagedDataAccess) for bulk inserts from .NET — it bypasses per-row overhead and is significantly faster than individual `INSERT` statements.
- [INST0016] **Don't** apply functions to indexed columns in `WHERE` clauses (e.g., `WHERE TRUNC(created_at) = …`) unless a matching function-based index exists — function calls prevent index range scans and force full table scans.
- [INST0017] **Don't** use `SELECT … FOR UPDATE` without `SKIP LOCKED` or `NOWAIT` unless you intend to block — the default behaviour waits indefinitely when another session holds the lock, which can stall application threads.

### PL/SQL

- [INST0018] **Do** use packages to group related procedures, functions, types, and cursors — packages provide encapsulation, reduce name collisions, allow private implementation details, and enable the optimizer to pin compiled code in memory.
- [INST0019] **Do** handle expected errors with named `EXCEPTION` handlers (`WHEN no_data_found`, `WHEN dup_val_on_index`) — catching `OTHERS` without re-raising hides bugs; always re-raise or log unexpected exceptions.
- [INST0020] **Do** use `RAISE_APPLICATION_ERROR(-20xxx, 'message')` to surface application-level errors — the `-20000` to `-20999` range is reserved for user-defined errors and integrates with .NET `OracleException.Number`.
- [INST0021] **Don't** use implicit cursors in loops for DML (`INSERT`, `UPDATE`, `DELETE`) when processing many rows — implicit cursors issue one SQL statement per iteration; use `FORALL` with a bulk-collected collection instead.

### Schema & Migrations

- [INST0022] **Do** use `TIMESTAMP WITH TIME ZONE` for all date-time columns that must preserve the originating time zone — `DATE` and `TIMESTAMP` store no time-zone information; `TIMESTAMP WITH TIME ZONE` prevents off-by-one-hour bugs across DST boundaries.
- [INST0023] **Do** use sequences with `GENERATED ALWAYS AS IDENTITY` (Oracle 12c+) for surrogate keys — it is the SQL standard, avoids manual sequence management, and integrates cleanly with EF Core.
- [INST0024] **Do** always include idempotent checks in migration scripts — use `BEGIN … EXCEPTION WHEN … END;` anonymous blocks or query `USER_TABLES`/`USER_TAB_COLUMNS` before `CREATE` or `ALTER` to prevent duplicate-object errors on re-run.
- [INST0025] **Do** name constraints explicitly (e.g., `CONSTRAINT pk_users PRIMARY KEY`, `CONSTRAINT uq_users_email UNIQUE`) — Oracle auto-generates `SYS_C00xxxxx` names that are impossible to reference in rollback scripts.
- [INST0026] **Don't** use `VARCHAR` — use `VARCHAR2`; Oracle's `VARCHAR` is reserved and may change semantics in future releases; `VARCHAR2` is the standard string type.
