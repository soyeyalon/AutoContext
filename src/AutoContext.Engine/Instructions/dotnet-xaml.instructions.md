---
name: "dotnet-xaml (v1.0.0)"
description: "Apply when writing or reviewing XAML markup for WPF, .NET MAUI, WinUI, or Avalonia (layout, data binding, resources, templates)."
applyTo: "**/*.xaml"
---

# XAML Instructions

## MCP Tool Validation

No corresponding MCP tool is currently available to automatically
validate XAML markup (`.xaml`) — apply these instructions manually.
For any C# code-behind files touched in the same change, follow the
C# instructions and call `analyze_csharp_code_style` on those `.cs` files.

## Rules

### Layout

- [INST0001] **Do** use `Grid` with `ColumnDefinition`/`RowDefinition` for multi-row or multi-column layouts — avoid approximating a grid with nested `StackPanel` or `StackLayout` elements, which add unnecessary measure/arrange passes.
- [INST0002] **Do** use proportional (`*`) or `Auto` sizing on grid rows and columns — hardcoded pixel values break at different DPI scales and window sizes.
- [INST0003] **Don't** nest layout containers more than three levels deep — excessive nesting degrades layout performance and is a sign that the structure should be flattened or extracted into a reusable control.

### Data Binding

- [INST0004] **Do** bind UI properties to ViewModel properties or commands rather than setting values in code-behind — direct property access from code-behind bypasses change notification and breaks the MVVM contract.
- [INST0005] **Do** specify `Mode=OneWay` on bindings that do not need to write back — omitting the mode defaults to `TwoWay` on editable controls, adding unnecessary overhead and potential unexpected writes.
- [INST0006] **Do** set `FallbackValue` and `TargetNullValue` on bindings whose source may be absent or null — without them, the control displays nothing or throws a binding error.
- [INST0007] **Do** prefer `IValueConverter` or `StringFormat` over complex multi-binding expressions — converters are testable, reusable, and easier to debug than inline XAML logic.

### Resources & Styles

- [INST0008] **Do** define reusable styles, brushes, and templates in `ResourceDictionary` files rather than inline — centralises theming and makes resources available across the application.
- [INST0009] **Do** use `x:Key` for resources applied selectively; omit `x:Key` and rely on `TargetType` as the implicit key for styles that should apply automatically to all controls of that type.
- [INST0010] **Do** prefer `StaticResource` over `DynamicResource` unless the resource changes at runtime (e.g., theme switching) — `StaticResource` resolves once at parse time while `DynamicResource` performs a lookup on every access.
- [INST0011] **Don't** duplicate identical setters or brushes across multiple elements — extract them into a shared style or resource.

### Templates

- [INST0012] **Do** use `DataTemplate` to define how data objects are rendered — this separates presentation from data and avoids building UI elements programmatically.
- [INST0013] **Do** use `ControlTemplate` only when fully replacing a control's visual structure — for appearance tweaks, prefer `Style` setters and triggers instead.

### Naming & Structure

- [INST0014] **Do** use `x:Name` sparingly — if you need a name to manipulate a control from code-behind, consider whether a binding or command would be more appropriate.
- [INST0015] **Do** keep XAML files focused on a single view or component — extract reusable sections into `UserControl` or `ContentView`.

### Accessibility

- [INST0016] **Do** set `AutomationProperties.Name` on interactive controls that lack visible text labels — screen readers rely on this property to announce the control's purpose.
- [INST0017] **Don't** use colour alone to convey state — pair colour changes with text, icons, or `AutomationProperties.HelpText` so the UI remains usable for colour-blind users.
