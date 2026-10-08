---
name: "dotnet-aspnetcore (v1.0.0)"
description: "Apply when writing or reviewing ASP.NET Core Web APIs (middleware, routing, error handling, auth, model validation, HttpClient)."
applyTo: "**/*.{cs,fs,vb}"
---

# ASP.NET Core / Web API Instructions

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

- [INST0001] **Do** keep endpoint handlers and controller actions thin — delegate business logic to injected services; the handler should only map HTTP concerns (route/query params, status codes) to domain calls.
- [INST0002] **Do** register middleware in the documented order: `UseForwardedHeaders` → `UseHttpsRedirection` → `UseStaticFiles` → `UseRouting` → `UseCors` → `UseAuthentication` → `UseAuthorization` → `UseAntiforgery` → `UseRateLimiter` → endpoints. Misordering `UseAuthorization` before `UseRouting` silently disables endpoint authorization.
- [INST0003] **Do** use `UseExceptionHandler` (or `IExceptionHandler`) and return RFC 9457 Problem Details (`ProblemDetails`/`ValidationProblemDetails`) for all error responses — never expose raw exception details outside the `Development` environment.
- [INST0004] **Do** validate request models with Data Annotations or `IValidatableObject` — invalid input should never reach business logic.
- [INST0005] **Do** use `IHttpClientFactory` (or typed/named clients) to obtain `HttpClient` instances — creating and disposing `HttpClient` directly exhausts sockets and causes DNS stale-entry bugs.
- [INST0006] **Do** accept `CancellationToken` in endpoint handlers and controller actions and pass it through to async calls — the token fires when the client disconnects, allowing early cancellation of database queries and HTTP calls.
- [INST0007] **Do** use policy-based authorization (`[Authorize(Policy = "…")]` / `RequireAuthorization("…")`) rather than role checks scattered through code — policies centralise access rules and are easier to test and audit.
- [INST0008] **Do** use `IAsyncEnumerable<T>` or pagination for large result sets — materialising an entire collection into memory and serialising it in one response harms latency, memory, and throughput.
- [INST0009] **Do** offload long-running work to a background service (`BackgroundService`/`IHostedService`) or a message queue — never block the HTTP request thread waiting for CPU-intensive or minutes-long operations.
- [INST0010] **Do** make all endpoint handlers and controller actions `async` and call data access / I/O APIs asynchronously — synchronous reads or writes on `HttpRequest`/`HttpResponse` bodies can starve the thread pool under load.
- [INST0011] **Don't** enable the Developer Exception Page in production — it exposes stack traces, source code, and environment variables; use `UseExceptionHandler` with a generic error payload instead.
- [INST0012] **Don't** call `Task.Run` and immediately `await` it inside a request handler — ASP.NET Core already runs on thread-pool threads, so the extra scheduling adds overhead without benefit.
- [INST0013] **Don't** store per-request state in `static` fields or singleton services — ASP.NET Core processes many requests concurrently; use `Scoped` services or `HttpContext.Items` for request-scoped data.

### Minimal APIs

- [INST0014] **Do** prefer Minimal APIs (`app.MapGet`, `app.MapPost`, …) for new endpoints — they have less overhead, simpler testing, and less boilerplate than controller-based APIs. Reserve controllers when you need advanced model binding, `JsonPatch`, or OData support.
- [INST0015] **Do** return `TypedResults` (e.g., `TypedResults.Ok(value)`, `TypedResults.NotFound()`) instead of `Results` — typed results carry compile-time response metadata for OpenAPI generation and enable type assertions in tests.
- [INST0016] **Do** use `RouteGroupBuilder` (`app.MapGroup("/api/users")`) to share prefixes, filters, and metadata across related endpoints — avoids repeating `.RequireAuthorization()` and `.WithTags()` on every endpoint.
- [INST0017] **Do** extract endpoint definitions into static extension methods (e.g., `MapUserEndpoints(this IEndpointRouteBuilder)`) once a group exceeds roughly five endpoints — keeps `Program.cs` readable.
- [INST0018] **Do** use `WithName()` and `WithTags()` on every endpoint — `WithName` enables link generation via `LinkGenerator` and `WithTags` organises OpenAPI documentation.
- [INST0019] **Do** use endpoint filters (`AddEndpointFilter<T>`) for cross-cutting concerns (validation, logging, auth checks) — they are the Minimal API equivalent of action filters and follow the same onion model. For model validation, use `.WithParameterValidation()` (via `MinimalApis.Extensions`) or a custom validation filter.
- [INST0020] **Do** use `[AsParameters]` to bind multiple route, query, and header values into a single record — reduces parameter clutter and makes the handler signature readable.
- [INST0021] **Do** implement `IBindableFromHttpContext<T>` or a static `BindAsync` method for custom complex types that don't fit default binding — avoids manual parsing in every handler.
- [INST0022] **Do** annotate endpoints with `Produces<T>(statusCode)` and `ProducesValidationProblem()` when the response types cannot be inferred — explicit metadata gives accurate OpenAPI documentation.
- [INST0023] **Do** test Minimal API endpoints via `WebApplicationFactory<Program>` and `HttpClient` — this exercises the full middleware pipeline including filters, auth, and serialisation.
- [INST0024] **Don't** use `[FromForm]` without antiforgery — form-based submissions are vulnerable to CSRF unless antiforgery validation is enabled.
