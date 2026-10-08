---
name: "dotnet-signalr (v1.0.0)"
description: "Apply when writing or reviewing SignalR hubs, real-time connections, group management, or backplane configuration."
applyTo: "**/*.{cs,fs,vb}"
---

# SignalR Instructions

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

### Hub Design

- [INST0001] **Do** keep hubs as thin dispatchers — inject services for business logic rather than implementing it in hub methods.
- [INST0002] **Do** use strongly-typed hubs (`Hub<IClientProxy>`) to get compile-time safety on client method names.
- [INST0003] **Do** use `[Authorize]` or any known project-specific attribute that derives from it on hubs or individual methods to enforce authentication.
- [INST0004] **Don't** store per-connection state in hub instance fields — hubs are transient; use `Groups`, `ConnectionId` mapping, or an external store.

### Groups & Connections

- [INST0005] **Do** use groups for topic-based broadcasting (e.g., `Clients.Group("room-42").SendAsync(...)`) — avoid iterating connection IDs manually.
- [INST0006] **Do** track connection lifecycle via `OnConnectedAsync` / `OnDisconnectedAsync` for presence tracking or resource cleanup.
- [INST0007] **Don't** assume a single connection per user — a user can have multiple tabs/devices; use groups or `Clients.User(userId)` for user-level messaging.

### Scaling & Reliability

- [INST0008] **Do** configure a backplane (Redis, Azure SignalR Service, or SQL Server) when running behind a load balancer — without it, messages only reach clients connected to the same server.
- [INST0009] **Do** handle reconnection gracefully on the client — implement `onreconnecting` / `onreconnected` callbacks and re-join groups after reconnect.
- [INST0010] **Don't** send large payloads over SignalR — it is designed for small, frequent messages; use a download endpoint for bulk data.
- [INST0011] **Don't** use SignalR for request/response patterns — use REST or gRPC for operations where the caller needs a direct reply.
