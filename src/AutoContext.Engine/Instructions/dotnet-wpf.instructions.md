---
name: "dotnet-wpf (v1.0.0)"
description: "Apply when writing or reviewing WPF code, XAML views, ViewModels, data bindings, commands, or WPF-specific patterns."
applyTo: "**/*.{cs,vb,xaml}"
---

# WPF Instructions

> These instructions cover WPF-specific patterns — MVVM architecture, data binding, threading, performance, and XAML structure.

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

### MVVM & Commands

- [INST0001] **Do** follow the MVVM pattern — ViewModels must not reference View types; express user interactions through commands and data binding rather than code-behind event handlers, keeping the ViewModel testable without a UI host.
- [INST0002] **Do** use `ICommand` via `RelayCommand` or `DelegateCommand` (e.g., from CommunityToolkit.Mvvm) for user actions instead of wiring event handlers in code-behind — this decouples the action from the UI and makes ViewModels independently testable.
- [INST0003] **Do** implement `INotifyPropertyChanged` on ViewModel properties that the UI binds to — the WPF binding engine only detects value changes in `OneWay`/`TwoWay` bindings when this interface is implemented and `PropertyChanged` is raised.
- [INST0004] **Do** use `ObservableCollection<T>` for collections bound to `ItemsControl`, `ListBox`, or `DataGrid` — `List<T>` does not implement `INotifyCollectionChanged`, so additions and removals are invisible to the UI; `ObservableCollection<T>` is dramatically faster per update than rebinding a `List<T>` because it notifies the control of the single changed item instead of forcing a full re-render.
- [INST0005] **Don't** put business logic in code-behind — code-behind should only contain UI-specific code that cannot be expressed in XAML (e.g., focus management, drag-and-drop handling, animation triggers).
- [INST0006] **Don't** call `Window.Show()`, `MessageBox.Show()`, or navigate directly from a ViewModel — use a dialog or navigation service abstraction so the ViewModel remains testable without a live UI host.
- [INST0007] **Don't** use `FindName()`, `LogicalTreeHelper`, or manual `VisualTreeHelper` traversal to locate child controls — prefer property binding, commands, or attached behaviors, which are the approaches recommended by the WPF stylable controls guidelines.

### Threading & Performance

- [INST0008] **Do** use `async`/`await` with `Task.Run` for background work in ViewModels and marshal back to the UI thread only for the final property update — never block the UI thread with synchronous I/O or long computations.
- [INST0009] **Do** call `Freeze()` on `Brush`, `Geometry`, `Transform`, and other `Freezable` objects used repeatedly across elements — frozen objects skip change-notification overhead and can be safely shared across threads.
- [INST0010] **Do** rely on `VirtualizingStackPanel` (the default panel for `ListBox` and `ListView`) when displaying large item collections — virtualisation creates UI containers only for visible items, reducing rendering time from seconds to milliseconds for large data sets.
- [INST0011] **Do** use `StringFormat` in bindings for simple display formatting instead of a full `IValueConverter` for trivial cases — e.g., `{Binding Price, StringFormat='{}{0:C}'}`.
- [INST0012] **Do** use `WeakEventManager<TSource, TEventArgs>` when subscribing to events on long-lived sources (static events, singletons, `Application`-scoped objects) from short-lived ViewModels — direct handler registration creates a strong reference from source to subscriber that prevents GC and causes memory leaks, a pattern explicitly documented in the WPF weak event pattern guidelines.  
- [INST0013] **Do** set `DecodePixelWidth` and `DecodePixelHeight` on `BitmapImage` to match the display dimensions — WPF decodes to full source resolution by default; a 4000×3000 image displayed at 100×75 consumes ~46 MB instead of ~29 KB when decoded at display size, as flagged by the WPF imaging performance documentation.
- [INST0014] **Don't** update UI-bound properties from a background thread without marshalling to the UI thread — WPF uses a single-threaded apartment (STA) model; accessing UI objects from a non-UI thread throws `InvalidOperationException`.
- [INST0015] **Don't** disable UI virtualization on item controls with large datasets — avoid setting `VirtualizingStackPanel.IsVirtualizing="False"` or replacing the `ItemsPanel` with a non-virtualizing panel, as this forces all item containers to be created and retained in memory simultaneously.

### Controls & Validation

- [INST0016] **Do** implement custom control properties that need data binding, animation, style setters, or property value inheritance as `DependencyProperty` — CLR-only properties silently break all WPF property-system features; register with `DependencyProperty.Register` and expose them through a CLR wrapper that calls `GetValue`/`SetValue`.
- [INST0017] **Do** implement `INotifyDataErrorInfo` on ViewModels that perform input validation — it supersedes `IDataErrorInfo`, supports multiple errors per property, cross-property errors, and async validation; WPF surfaces errors automatically via `ValidatesOnNotifyDataErrors` (which defaults to `true` in bindings).
- [INST0018] **Do** use `UserControl` when composing existing controls into a reusable unit, and derive from `Control` with a `ControlTemplate` (a custom control) only when you need a fully lookless control that consumers can re-template — mixing them up leads to sealed visual structures or unnecessarily complex control authoring.
