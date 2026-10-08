---
name: "dotnet-aspx (v1.0.0)"
description: "Apply when writing or reviewing ASP.NET Web Forms pages (.aspx, .ascx, .master) or their code-behind."
applyTo: "**/*.{aspx,ascx,master}"
---

# ASP.NET Web Forms Instructions

> These instructions cover ASP.NET Web Forms — page lifecycle, server controls, ViewState, data binding, and security. They apply to legacy and maintained Web Forms codebases.

## MCP Tool Validation

No corresponding MCP tool is currently available to automatically
validate ASP.NET Web Forms markup (`.aspx`, `.ascx`, `.master`) — apply
these instructions manually. For any C# code-behind files touched in
the same change, follow the C# instructions and call
`analyze_csharp_code_style` on those `.cs` files.

## Rules

### Page Lifecycle & Events

- [INST0001] **Do** place initialization logic in `Page_Init` or `Page_Load` with an `if (!IsPostBack)` guard — code that runs on every postback without the guard causes duplicate data binding, resets user input, and wastes server resources.
- [INST0002] **Do** understand the page lifecycle order (`Init → Load → Validation → Event Handling → Rendering → Unload`) — placing logic in the wrong event causes silent data loss or controls that ignore user input.
- [INST0003] **Don't** add dynamic controls after `Init` — controls added later miss ViewState restoration and event binding, leading to controls that lose their values on postback.

### ViewState & State Management

- [INST0004] **Do** disable ViewState on controls that do not need it (`EnableViewState="false"`) — ViewState serializes the entire control tree into a hidden field, inflating page size; a `GridView` with 100 rows can add hundreds of kilobytes to every postback.
- [INST0005] **Do** use `Session` or `Cache` for large objects instead of ViewState — ViewState travels with every request and response, increasing bandwidth and parse overhead.
- [INST0006] **Don't** store sensitive data in ViewState — even with `ViewStateEncryptionMode="Always"`, ViewState is a client-side blob that can be tampered with if the machine key is compromised.

### Server Controls & Data Binding

- [INST0007] **Do** use `<%# Eval("Property") %>` for one-way display binding and `<%# Bind("Property") %>` only when two-way binding is needed — `Bind` adds overhead for extracting values on postback.
- [INST0008] **Do** prefer `Repeater` over `GridView` or `DataList` when you only need read-only templated output — `Repeater` renders no extra HTML markup, giving full control over the output.
- [INST0009] **Don't** use `AutoPostBack="true"` on controls unless immediate server processing is required — each auto-postback causes a full-page round trip; consider client-side JavaScript or `UpdatePanel` for partial updates.

### Security

- [INST0010] **Do** validate all input server-side with `Page.IsValid` after calling `Validate()` — client-side validators are trivially bypassed by disabling JavaScript.
- [INST0011] **Do** use parameterized queries or stored procedures for all data access — Web Forms inline `SqlDataSource` controls with string-concatenated SQL are a common source of SQL injection.
- [INST0012] **Don't** disable request validation (`ValidateRequest="false"`) unless you have a specific need and apply your own sanitization — request validation is a defence-in-depth layer against XSS.
- [INST0013] **Don't** expose server-side paths or stack traces to the client — set `<customErrors mode="On">` and configure a generic error page.

### Performance

- [INST0014] **Do** enable output caching (`<%@ OutputCache %>`) on pages or user controls that display data that changes infrequently — this avoids re-executing the full page lifecycle on every request.
- [INST0015] **Don't** use `Response.Redirect` in a path that will execute further code — call `Response.Redirect(url, false)` followed by `Context.ApplicationInstance.CompleteRequest()` to avoid the `ThreadAbortException` thrown by the default overload.

### ASP.NET AJAX

- [INST0016] **Do** prefer `PageMethods` (static `[WebMethod]` on the page) or `ScriptService` (`[ScriptService]` web service) for lightweight AJAX calls — they return JSON directly without a full page lifecycle round trip.
- [INST0017] **Do** set `UpdateMode="Conditional"` on every `UpdatePanel` and call `Update()` explicitly or use specific triggers — the default `Always` mode refreshes the panel on every async postback on the page, even unrelated ones.
- [INST0018] **Don't** wrap large or complex UI regions in an `UpdatePanel` — `UpdatePanel` still executes the entire page lifecycle server-side and sends the full control HTML back; for anything beyond a simple partial refresh, use `PageMethods` or a dedicated service endpoint with client-side DOM updates.
