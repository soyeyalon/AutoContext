---
name: "dotnet-maui (v1.0.0)"
description: "Apply when writing or reviewing .NET MAUI apps (MVVM, Shell navigation, data binding, layouts, CollectionView, DI, UI thread)."
applyTo: "**/*.{cs,xaml}"
---

# .NET MAUI Instructions

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

### MVVM & Navigation

- [INST0001] **Do** apply the MVVM pattern — bind controls to ViewModel properties and commands via data bindings and keep all business logic out of code-behind files.
- [INST0002] **Do** use Shell as the primary navigation container and navigate with `Shell.Current.GoToAsync` using URI-based routes — Shell integrates directly with the DI container and constructs pages with constructor injection automatically.
- [INST0003] **Do** register all pages, ViewModels, and services with the DI container in `MauiProgram.CreateMauiApp` — Shell resolves constructor dependencies when navigating to registered routes.
- [INST0004] **Don't** reference UI types (`Button`, `Label`, `Page`, etc.) from ViewModels — ViewModels must remain UI-free so they can be unit-tested in isolation without a running MAUI application.

### Data Binding & Performance

- [INST0005] **Do** use compiled bindings — set `x:DataType` on XAML elements to enable compile-time binding resolution, which is 8-20× faster than runtime reflection-based bindings.
- [INST0006] **Do** expose ViewModel commands as `ICommand` (e.g., `AsyncRelayCommand` from `CommunityToolkit.Mvvm`) and bind control `Command` properties to them — handling events in code-behind bypasses MVVM and makes commands untestable.
- [INST0007] **Do** use async methods in ViewModels for all I/O operations to keep the UI thread responsive — MAUI automatically marshals data-binding updates to the UI thread, so ViewModel properties can be set from any thread.
- [INST0008] **Do** marshal direct UI manipulation to the main thread via `MainThread.BeginInvokeOnMainThread` or `Dispatcher.DispatchAsync` — any non-binding code that touches UI elements (controls, pages) must run on the UI thread.
- [INST0009] **Don't** use data bindings for content that never changes — setting `Button.Text = "OK"` directly has less overhead than binding to a ViewModel string property with a static value; use bindings only when the value changes at runtime.

### Layout & Resources

- [INST0010] **Do** use `CollectionView` instead of `ListView` for scrollable data lists — `CollectionView` is more flexible, more performant, and supports layout configurations and multiple selection that `ListView` does not.
- [INST0011] **Do** place `CollectionView` inside a `Grid` row with `*` height, not inside a `VerticalStackLayout` — nesting inside a `StackLayout` prevents scrolling, breaks `ScrollTo`, and limits the number of visible items.
- [INST0012] **Do** choose the right layout for the job — use `Grid` for multi-row/column forms rather than composing nested `HorizontalStackLayout` elements that approximate a `Grid`; unnecessary nesting adds extra measure passes.
- [INST0013] **Do** implement `IQueryAttributable` to receive Shell navigation parameters instead of using `[QueryPropertyAttribute]` — `[QueryPropertyAttribute]` is not trim-safe and breaks NativeAOT; `ApplyQueryAttributes` works with full trimming.
- [INST0014] **Do** scope page-specific XAML resources in the page's `ResourceDictionary` rather than in `App.xaml` — app-level resources are parsed at startup even if the page is never visited.
- [INST0015] **Do** place image assets in `Resources/Images/` with the `MauiImage` build action — MAUI automatically resizes and packages images at the correct resolution for each platform, eliminating manual per-platform image duplication; prefer SVG format for icons and logos so they scale without quality loss.
- [INST0016] **Do** tie image stream creation and disposal to `Page.Appearing`/`Page.Disappearing`, and configure `UriImageSource.CacheValidity` for remote images to avoid re-downloading on every navigation — images are among the most memory-intensive resources and unreleased streams cause unnecessary memory pressure.

### Platform & Deployment

- [INST0017] **Do** implement platform-specific behavior using partial classes in `Platforms/<Platform>/` subfolders or filename-based multi-targeting (`MyService.Android.cs`, `MyService.iOS.cs`, `MyService.Windows.cs`) — avoid `#if ANDROID` / `#if IOS` preprocessor blocks inside shared cross-platform code, which mixes platform concerns into shared logic and reduces readability.
- [INST0018] **Do** implement custom native controls by subclassing `ViewHandler<TVirtualView, TPlatformView>` with platform-specific partial classes — the `ViewRenderer` pattern from Xamarin.Forms is deprecated in .NET MAUI.
- [INST0019] **Do** resolve all ILLink trimming warnings before shipping a release build — any unfixed warning indicates code that may be silently removed by the linker, causing runtime exceptions on device; avoid `Type.GetType(string)`, `Assembly.Load`, and `System.Reflection.Emit` in MAUI code as these prevent static analysis and block NativeAOT compatibility.
