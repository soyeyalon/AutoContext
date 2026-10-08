---
name: "dotnet-winforms (v1.0.0)"
description: "Apply when writing or reviewing Windows Forms code (forms, controls, data binding, threading, layout, validation)."
applyTo: "**/*.{cs,vb}"
---

# Windows Forms Instructions

> These instructions cover WinForms-specific patterns — form and control design, threading, data binding, layout, validation, and resource management.

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

### Architecture & Layout

- [INST0001] **Do** keep `Form` classes as thin presentation shells — event handlers should immediately delegate to service or presenter classes, leaving the form responsible only for updating controls and routing input; this keeps business logic independently testable without a live UI host.
- [INST0002] **Do** use `Control.InvokeAsync` (.NET 9+) to marshal updates to the UI thread from async code — it posts non-blocking to the message queue, returns an awaitable `Task`, and avoids the deadlock risk of `Control.Invoke`; on earlier frameworks check `InvokeRequired` and call `Invoke` if true.
- [INST0003] **Do** wrap programmatic bulk control additions and repositioning with `SuspendLayout()` / `ResumeLayout(false)` — the Microsoft API docs state explicitly: *"it is recommended that you call SuspendLayout before initializing controls to be added...this will increase the performance of applications with many controls."*
- [INST0004] **Do** set `DoubleBuffered = true` (or `SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true)`) on any form or control with custom `OnPaint` logic — this renders to an off-screen buffer before blitting to the screen, eliminating the flicker produced by multi-step paint operations.
- [INST0005] **Do** extract repeated groups of controls into `UserControl` subclasses — they are designer-compatible, encapsulate layout and event wiring, and eliminate duplicated initialization code across forms.
- [INST0006] **Do** use `ErrorProvider.SetError` to show inline validation messages next to the offending control rather than `MessageBox.Show` — the official ErrorProvider overview states it is *"a better alternative...because once a message box is dismissed, the error message is no longer visible."*
- [INST0007] **Do** use `TableLayoutPanel` and `FlowLayoutPanel` for control layout instead of hard-coded `Location`/`Size` pixel values — `AutoScaleMode` can correctly re-flow panel-based layouts when DPI or font size changes but cannot adjust absolute pixel coordinates.
- [INST0008] **Do** override `Dispose(bool disposing)` to unsubscribe from any events on long-lived objects (singletons, static events, `Application`-scoped objects) that the form or control subscribed to — the event source holds a strong reference to the subscriber, preventing GC until the source itself is collected.
- [INST0009] **Don't** access controls from non-UI threads — WinForms controls are single-threaded (STA); accessing a control from a background thread throws `InvalidOperationException: Cross-thread operation not valid: Control accessed from a thread other than the thread it was created on.`
- [INST0010] **Don't** use `BackgroundWorker` in new code — Microsoft retained it *"for backward compatibility"* only; `Task` + `async`/`await` with `Control.InvokeAsync` is the modern replacement and integrates cleanly with cancellation, exception propagation, and the `async`/`await` model.

### Data Binding & Grids

- [INST0011] **Do** bind data-aware controls through a `BindingSource` rather than directly to lists or datasets — the official DataGridView documentation calls it the *"preferred data source"* because it provides currency management, sorting, filtering, and change notification for sources that do not implement `IBindingList`.
- [INST0012] **Do** set `DataGridView.VirtualMode = true` and handle `CellValueNeeded`/`CellValuePushed` events when displaying large collections — in bound mode the control loads all rows into memory, making virtual mode *"necessary to optimize performance"* for very large datasets per the official virtual mode guidance.
- [INST0013] **Do** use `BindingList<T>` instead of `List<T>` for in-memory editable data sources — it implements `IBindingList` and automatically notifies bound controls of item additions, removals, and property changes without requiring a manual `BindingSource.ResetBindings` call.
- [INST0014] **Don't** iterate over `DataGridView.Rows` with a `foreach` loop or index the `Rows` collection directly — the Best Practices documentation states: *"Avoid indexing the Rows collection or iterating through it with a foreach loop"* because doing so forces shared rows to become unshared, negating the row-sharing memory optimisation; use row-index-based event arguments or dedicated API methods instead.

### DPI, Startup & Resources

- [INST0015] **Do** call `ApplicationConfiguration.Initialize()` as the first statement in `Main` on .NET 6+ — it applies `<ApplicationHighDpiMode>`, `<ApplicationVisualStyles>`, and `<ApplicationDefaultFont>` MSBuild properties to the process before any form is created; omitting it produces compiler warning `WFO0003` and silently falls back to legacy defaults.
- [INST0016] **Do** set `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` in your `.csproj` — it enables per-child-window DPI notifications and improved comctl32 scaling so each monitor uses its own DPI; the default `SystemAware` mode applies the primary monitor's DPI to all monitors, causing blurry controls on secondary displays.
- [INST0017] **Do** wrap every `Brush`, `Pen`, and `Font` object created inside `OnPaint` in a `using` statement — these types hold unmanaged GDI handles that are not released promptly by the GC; always use the `Graphics` instance supplied by `PaintEventArgs`, never dispose it (it is framework-owned), and dispose any `Graphics` you create yourself (e.g., from a `Bitmap`) with `using` as well.
- [INST0018] **Do** set `DataGridViewCellStyle` properties at the `DataGridView` or `DataGridViewColumn` level rather than on individual `DataGridViewCell` or `DataGridViewRow` objects — the Best Practices for Scaling documentation states this directly: *"Avoid setting cell style properties for individual DataGridViewCell or DataGridViewRow objects"* because each element-level assignment allocates a separate style object, increasing memory and GC pressure.
- [INST0019] **Don't** rely on `Form.Closing` or `FormClosed` events for resource cleanup — these events are not raised in all termination paths (e.g., `Application.Exit`, process kill); always put event unsubscription and resource release in `Dispose(bool disposing)` instead.
