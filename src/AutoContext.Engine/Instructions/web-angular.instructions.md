---
name: "web-angular (v1.0.0)"
description: "Apply when writing or reviewing Angular apps (standalone components, signals, change detection, DI, routing, RxJS)."
applyTo: "**/*.{ts,mts,cts,html}"
---

# Angular Instructions

> These instructions target Angular applications — component design, reactivity, dependency injection, RxJS, routing, and forms.

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content` and the file's absolute
path as `filePath`. Treat any reported violation as blocking — fix it
before reporting the work as done.

## Rules

### Components & Templates

- [INST0001] **Do** create standalone components (`standalone: true`) — NgModule-based components are deprecated as the primary authoring format in Angular 17+; standalone components can be bootstrapped, imported, and lazy-loaded directly without a declaring module.
- [INST0002] **Do** set `changeDetection: ChangeDetectionStrategy.OnPush` on every component — it prevents Angular from checking the component on every application event and limits checks to when an `@Input` reference changes, a signal read by the template emits a new value, or the component explicitly calls `markForCheck()`.
- [INST0003] **Do** use the built-in control flow syntax (`@if`, `@for`, `@switch`, `@defer`) introduced in Angular 17 instead of the structural directives `*ngIf`, `*ngFor`, and `*ngSwitch` — the new syntax compiles to optimized code, produces smaller bundles, and avoids operator-precedence issues with `*ngIf="a && b"`.
- [INST0004] **Do** include a `track` expression in every `@for` block (e.g., `@for (item of items; track item.id)`) — without tracking, Angular destroys and recreates all DOM nodes on any collection change, causing layout thrash and lost focus state.
- [INST0005] **Do** use `@defer` to lazy-load heavy components or third-party widgets that are not needed on initial render — pair it with `@loading`, `@placeholder`, and `@error` blocks to handle all loading states gracefully.
- [INST0006] **Don't** call component methods from templates for derived values — Angular evaluates template expressions on every change-detection cycle; use `computed()` signals or pure pipes instead.

### Signals & Reactivity

- [INST0007] **Do** use `signal()` for local component state that triggers change detection — signals are synchronous, granular, and eliminate the boilerplate of `ngOnChanges` or `BehaviorSubject` for simple local state.
- [INST0008] **Do** use `computed()` to derive values from one or more signals — computed signals are memoized and re-evaluated only when their dependencies change, not on every render.
- [INST0009] **Do** use `effect()` only for side effects that must synchronize with signal changes (e.g., persisting to `localStorage`, calling a DOM API) — never use `effect()` to update another signal; use `computed()` instead to avoid circular dependency errors.
- [INST0010] **Do** use the `async` pipe in templates to subscribe to Observables — it automatically unsubscribes on component destruction and triggers change detection without requiring manual subscription management in the component class.
- [INST0011] **Do** use `toSignal()` to convert an Observable to a signal when the value is consumed inside a `computed()` or by `OnPush` change detection — the resulting signal always holds the latest emitted value and participates in the signal graph.

### Dependency Injection

- [INST0012] **Do** use the `inject()` function to declare dependencies instead of constructor injection — `inject()` works in component classes, guards, resolvers, and interceptors written as plain functions, and reduces ceremony when a class has many dependencies.
- [INST0013] **Do** provide app-wide singleton services with `providedIn: 'root'` — this enables tree-shaking of unused services and avoids the overhead of listing the service in a module or component's `providers` array.
- [INST0014] **Do** provide feature-scoped services in the `providers` of a route or component to scope the service lifetime to that subtree — prevents stale state from leaking across feature areas and ensures the service is destroyed when the route is left.

### RxJS & Subscriptions

- [INST0015] **Do** manage Observable subscriptions with `takeUntilDestroyed()` in component and service code — pass an optional `DestroyRef` when outside an injection context to ensure cleanup when the injector is destroyed.
- [INST0016] **Do** compose reactive pipelines with RxJS operators (`switchMap`, `debounceTime`, `distinctUntilChanged`, `catchError`) instead of nesting subscriptions — flat pipelines are easier to cancel, test, and reason about.
- [INST0017] **Don't** subscribe inside another `subscribe` call — flatten nested Observables with `switchMap`, `mergeMap`, or `concatMap` to maintain a single subscription chain and proper cancellation semantics.

### Routing & Lazy Loading

- [INST0018] **Do** lazy-load feature routes using `loadComponent` for standalone components or `loadChildren` for route arrays — each lazy chunk is downloaded only when the user navigates to that route, reducing the initial bundle size.
- [INST0019] **Do** write route guards and resolvers as plain functions (`CanActivateFn`, `ResolveFn`) rather than injectable classes — functional guards are lighter, composable, and do not require registration in a `providers` array.
- [INST0020] **Don't** manipulate `window.location` or `window.history` directly — always navigate through Angular's `Router` to preserve in-app navigation state, route guards, and browser history integration.

### Reactive Forms

- [INST0021] **Do** use `ReactiveFormsModule` with typed `FormGroup<T>` for forms with validation, conditional fields, or dynamic controls — typed forms catch shape mismatches at compile time and make form values fully type-safe throughout the component.
- [INST0022] **Do** declare validators at the `FormControl` level using built-in validators (`Validators.required`, `Validators.email`) or custom validator functions that return `ValidationErrors | null` — keep validation logic out of component methods and out of templates.
- [INST0023] **Don't** use template-driven forms (`NgModel`) for complex or dynamic forms — they hide the form model from TypeScript, making programmatic control, validation, and testing significantly harder.

### Services & HTTP

- [INST0024] **Do** make all HTTP calls in services, not in components — components call a service method and bind to the returned Observable or signal; services own the HTTP logic and can be mocked in tests without a real network.
- [INST0025] **Do** type all `HttpClient` method calls explicitly (e.g., `get<MyResponseType>(url)`) — untyped calls return `Object` and lose compile-time safety throughout the data pipeline.
- [INST0026] **Do** handle HTTP errors with `catchError` in the service's Observable pipeline and return a meaningful fallback or re-throw a domain error — raw `HttpErrorResponse` objects should never reach template bindings.

### Code Organization

- [INST0027] **Do** follow Angular's file naming convention: `<feature>.component.ts`, `<feature>.service.ts`, `<feature>.pipe.ts`, `<feature>.guard.ts`, `<feature>.interceptor.ts` — one class per file, with the filename matching the class name in kebab-case.
- [INST0028] **Do** separate smart (container) components from dumb (presentational) components — container components own data fetching and state; presentational components receive data via `input()` signals and emit events via `output()` with no knowledge of the data source.
- [INST0029] **Do** use signal-based `input()` and `output()` functions (Angular 17.1+) instead of the `@Input()` and `@Output()` decorators for new components — they integrate with the signal graph, eliminate `ngOnChanges` ceremony, and make required inputs a compile-time check.
