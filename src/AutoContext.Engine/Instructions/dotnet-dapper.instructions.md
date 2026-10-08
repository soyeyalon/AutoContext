---
name: "dotnet-dapper (v1.0.0)"
description: "Apply when writing or reviewing Dapper data access (raw SQL, parameter binding, mapping, transactions, connection management)."
applyTo: "**/*.{cs,fs,vb}"
---

# Dapper Instructions

> These instructions target Dapper used as a micro-ORM in .NET projects.

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

### Connection Management

- [INST0001] **Do** create and dispose connections per unit of work with `using` — Dapper extends `IDbConnection`; the connection should not outlive the operation. The underlying ADO.NET pool handles reuse.
- [INST0002] **Do** open connections explicitly with `await connection.OpenAsync(cancellationToken)` before calling Dapper methods when you need to share a connection across multiple calls in the same transaction — Dapper opens the connection automatically for single calls, but explicit open avoids repeated open/close cycles in a batch.
- [INST0003] **Don't** register `IDbConnection` or `SqlConnection` as a singleton or scoped service and inject it into long-lived components — connection pooling is handled by ADO.NET; injecting a single instance risks concurrent use on the same connection, which is not thread-safe.

### Query Design

- [INST0004] **Do** always use parameterized queries (`@param` placeholders with an anonymous object or `DynamicParameters`) — never concatenate user input into SQL strings; Dapper parameterises automatically when you pass an object.
- [INST0005] **Do** use `QueryAsync<T>` and `QueryFirstOrDefaultAsync<T>` with a concrete type instead of `dynamic` — typed results catch mapping errors at compile time and avoid boxing overhead.
- [INST0006] **Do** use `QueryMultipleAsync` to execute multiple `SELECT` statements in a single round-trip and read each result set with `ReadAsync<T>` — avoids issuing separate queries when you need related data from different tables.
- [INST0007] **Do** use `CommandType.StoredProcedure` when calling stored procedures — this generates a proper TDS RPC call that correctly handles output parameters and return values, avoiding the overhead of parsing an `EXEC` wrapper batch.
- [INST0008] **Don't** use `Query<dynamic>` or `Query<IDictionary<string, object>>` as a default — dynamic results lose compile-time safety, make refactoring dangerous, and incur boxing for value types.
- [INST0009] **Don't** build SQL with string concatenation or interpolation for `WHERE … IN` clauses — pass an `IEnumerable<T>` parameter and Dapper expands it automatically (e.g., `WHERE Id IN @Ids`).

### Mapping

- [INST0010] **Do** use multi-mapping (`QueryAsync<T1, T2, TResult>` with a `splitOn` parameter) to map joined rows to related objects in a single query — avoids N+1 queries for parent-child relationships.
- [INST0011] **Do** register custom `SqlMapper.TypeHandler<T>` implementations for types that ADO.NET does not map natively (e.g., `DateOnly`, `TimeOnly`, strongly-typed IDs, enums stored as strings) — custom handlers keep conversion logic centralised and out of every query.
- [INST0012] **Do** match C# property names to SQL column names or use column aliases in the `SELECT` — Dapper maps by name; mismatched names silently produce default values instead of throwing.
- [INST0013] **Don't** rely on column-order matching — Dapper maps by column name, not ordinal position; reordering columns in a query should never break mapping.

### Transactions

- [INST0014] **Do** use `IDbTransaction` with Dapper's `transaction` parameter for multi-statement operations that must be atomic — pass the transaction to every `ExecuteAsync`/`QueryAsync` call within the scope.
- [INST0015] **Do** wrap transactional code in `try/catch` and call `Rollback()` on failure — Dapper does not manage transactions; you are responsible for commit and rollback.
- [INST0016] **Don't** mix Dapper calls with and without the `transaction` parameter inside the same unit of work — statements without the transaction parameter execute outside the transaction and are not rolled back on failure.

### Performance

- [INST0017] **Do** use `ExecuteAsync` with an `IEnumerable<T>` parameter for batch inserts and updates — Dapper executes the statement once per item but reuses the same prepared command, which is faster than building and parsing a new SQL string per row.
- [INST0018] **Do** prefer `QueryFirstOrDefaultAsync<T>` over `QueryAsync<T>().FirstOrDefault()` when you need a single row — `QueryFirstOrDefault` stops reading after the first row; `Query` materialises the entire result set.
- [INST0019] **Do** use `buffered: false` for large result sets that you process row-by-row — unbuffered queries stream rows from the database instead of loading them all into memory; dispose the reader when done.
- [INST0020] **Don't** call `QueryAsync` inside a tight loop when you can batch the work into a single query with `WHERE … IN @Ids` or a table-valued parameter — each call is a separate round-trip; batching reduces network latency.
