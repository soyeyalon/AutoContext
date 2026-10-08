---
name: "dotnet-mongodb (v1.0.0)"
description: "Apply when writing or reviewing MongoDB queries, aggregation pipelines, indexes, or data access in .NET projects."
applyTo: "**/*.{cs,fs,vb}"
---

# MongoDB Instructions

> These instructions target MongoDB used in .NET projects with MongoDB.Driver or MongoDB.EntityFrameworkCore.

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

- [INST0001] **Do** use parameterized filters with the MongoDB C# driver's builder API (`Builders<T>.Filter`) instead of raw JSON or string interpolation — string-built queries are vulnerable to NoSQL injection just as SQL queries are vulnerable to SQL injection.
- [INST0002] **Do** enable authentication and use SCRAM-SHA-256 or x.509 certificates — MongoDB ships with authentication disabled by default; an unauthenticated instance is open to anyone who can reach the network port.
- [INST0003] **Do** enable TLS for all connections (`?tls=true` in the connection string) — MongoDB traffic is unencrypted by default; without TLS, credentials and data travel in plaintext.
- [INST0004] **Don't** store secrets (passwords, API keys, tokens) in MongoDB documents or collection metadata — use application-level secrets management; any user with `find` permission on the collection can read the data.

### Schema & Data Modelling

- [INST0005] **Do** embed related data in the same document when the relationship is one-to-few and the embedded data is always read together — embedding avoids extra round-trips and leverages MongoDB's document-level atomicity.
- [INST0006] **Do** use references (store the `_id` of the related document) when the related entity is large, shared across many parents, or updated independently — referencing avoids unbounded document growth and keeps write amplification low.
- [INST0007] **Do** keep documents under the 16 MB BSON limit — if a document can grow without bound (e.g., an array of log entries), move the growing data into a separate collection.
- [INST0008] **Do** use `BsonClassMap` or `[BsonElement]` attributes to control serialisation explicitly — relying on default conventions couples your wire format to C# property names and makes schema evolution fragile.
- [INST0009] **Don't** use unbounded arrays that grow over time (e.g., `comments`, `events`) inside a single document — unbounded arrays cause document migrations when they outgrow their allocated space and eventually hit the 16 MB limit.

### Query Design

- [INST0010] **Do** use the typed driver API (`Find<T>`, `Builders<T>.Filter`, `Builders<T>.Projection`) over raw `BsonDocument` filters — typed queries catch field-name typos at compile time and are easier to refactor.
- [INST0011] **Do** project only the fields you need with `Builders<T>.Projection.Include` — returning entire documents wastes bandwidth and prevents covered-index queries.
- [INST0012] **Do** use the aggregation pipeline (`Aggregate<T>().Match().Group().Project()`) for complex transformations — the pipeline runs server-side and avoids pulling large data sets into the application.
- [INST0013] **Do** use `BulkWriteAsync` for batch inserts, updates, and deletes — `BulkWrite` sends all operations in a single round-trip and is significantly faster than individual calls in a loop.
- [INST0014] **Don't** use `$where` or JavaScript expressions in queries — `$where` disables index use, runs a JavaScript interpreter per document, and is a NoSQL injection vector if the expression contains user input.
- [INST0015] **Don't** use `Find` without a filter or with an empty filter on large collections in production — an unfiltered `Find` returns every document; use pagination with `Limit` and `Skip` or a cursor-based approach.

### Indexing & Performance

- [INST0016] **Do** create indexes on fields used in `Filter`, `Sort`, and `Group` operations — missing indexes force collection scans; use `explain()` or the Atlas Profiler to verify index usage.
- [INST0017] **Do** create compound indexes that match your most common query patterns (equality fields first, sort fields next, range fields last) — MongoDB uses a single index per query; a well-ordered compound index covers the entire filter+sort.
- [INST0018] **Do** build indexes during low-traffic windows or use a rolling index build on replica sets — on MongoDB 4.2+ background builds are the default, but index creation still competes for resources and can degrade query latency on large collections.
- [INST0019] **Do** set a TTL index on time-series or expiring data (`ExpireAfter`) — TTL indexes let MongoDB automatically delete documents after a specified duration, avoiding manual cleanup jobs.
- [INST0020] **Don't** create indexes on fields with very low cardinality (e.g., `boolean`, `status` with 2–3 values) as the sole index — low-selectivity indexes scan a large fraction of the collection and provide little benefit.
- [INST0021] **Don't** ignore the `explain()` output — `executionStats.totalDocsExamined` vs `nReturned` reveals whether a query is scanning far more documents than it returns, indicating a missing or suboptimal index.

### Transactions & Consistency

- [INST0022] **Do** use multi-document transactions (`StartSessionAsync` + `StartTransaction`) only when atomicity across multiple documents or collections is required — single-document operations are already atomic in MongoDB.
- [INST0023] **Do** set an appropriate read concern (`majority`, `local`, `linearizable`) and write concern (`w: "majority"`) based on your consistency requirements — the defaults (`local` read, `w: 1` write) can return stale reads and acknowledge writes before replication.
- [INST0024] **Don't** use transactions for operations that can be modelled as a single-document update — transactions add latency, hold locks, and are unnecessary when MongoDB's single-document atomicity suffices.
