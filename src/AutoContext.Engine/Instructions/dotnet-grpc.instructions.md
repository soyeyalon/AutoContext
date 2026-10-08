---
name: "dotnet-grpc (v1.0.0)"
description: "Apply when writing or reviewing gRPC services in .NET (.proto files, client/server implementation, streaming patterns)."
applyTo: "**/*.{cs,fs,vb,proto}"
---

# gRPC Instructions

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

### Proto Design

- [INST0001] **Do** treat proto field numbers as immutable — never reuse or reassign a field number after it has been published; use `reserved` to retire old numbers.
- [INST0002] **Do** use `string` for identifiers, `google.protobuf.Timestamp` for dates, and wrapper types (`google.protobuf.StringValue`) when you need to distinguish null from default.
- [INST0003] **Do** version proto packages (e.g., `package myservice.v1;`) — breaking changes go in a new version.
- [INST0004] **Don't** use enums with a default value of `0` that carries business meaning — reserve `0` for `UNSPECIFIED` so missing values are detectable.

### Service Implementation

- [INST0005] **Do** set deadlines on every RPC call from the client — calls without deadlines can hang indefinitely.
- [INST0006] **Do** propagate `CancellationToken` through the call chain — respect `context.CancellationToken` in server handlers.
- [INST0007] **Do** return appropriate gRPC status codes — never return `OK` with an error payload; use `NotFound`, `InvalidArgument`, `PermissionDenied`, etc.
- [INST0008] **Do** use server-streaming for large result sets and client-streaming for uploads — avoid loading entire datasets into a single response message.
- [INST0009] **Don't** throw raw exceptions from gRPC handlers — catch and convert to `RpcException` with a meaningful `StatusCode` and detail message.
- [INST0010] **Don't** use gRPC for browser clients without a proxy — browsers cannot make HTTP/2 gRPC calls directly; use gRPC-Web or an Envoy/YARP proxy.

### Performance

- [INST0011] **Do** reuse `GrpcChannel` instances — creating a channel per call wastes TCP connections and TLS handshakes.
- [INST0012] **Don't** mix JSON and Protobuf serialization in the same service boundary — use the generated Protobuf message types end-to-end.
