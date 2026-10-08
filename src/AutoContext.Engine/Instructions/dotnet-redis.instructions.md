---
name: "dotnet-redis (v1.0.0)"
description: "Apply when writing or reviewing Redis caching, session storage, pub/sub, or distributed locking in .NET projects."
applyTo: "**/*.{cs,fs,vb}"
---

# Redis Instructions

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

- [INST0001] **Do** reuse a single `ConnectionMultiplexer` instance per application — it is thread-safe and expensive to create; register it as a singleton.
- [INST0002] **Do** use short, structured key names with a colon-separated namespace (e.g., `tenant:42:session:abc`) — keeps keys scannable and avoids collisions.
- [INST0003] **Do** set an explicit TTL on every key unless you have a deliberate reason for it to live forever — unbounded keys are a memory leak.
- [INST0004] **Do** use `SCAN` to iterate keys — never `KEYS *` in production, it blocks the server.
- [INST0005] **Do** use `IDistributedCache` or `IConnectionMultiplexer` via DI — avoid creating connections manually in business logic.
- [INST0006] **Do** use `MGET` / `MSET` for batch operations — round-trips are the primary latency cost.
- [INST0007] **Do** prefer `StringSet` with `When.NotExists` for simple distributed locks — or use RedLock for multi-instance correctness.
- [INST0008] **Don't** store large objects (>100 KB) as single values — consider breaking them up or using Hashes for partial reads.
- [INST0009] **Don't** treat Redis as a primary database — it is a cache and ephemeral store; data must be recoverable from the source of truth.
- [INST0010] **Don't** use `FlushDb` / `FlushAll` outside of test environments.
