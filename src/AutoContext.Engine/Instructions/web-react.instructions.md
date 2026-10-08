---
name: "web-react (v1.0.0)"
description: "Apply when writing or reviewing React components, Hooks, state, effects, list rendering, or component trees."
applyTo: "**/*.{js,jsx,mjs,cjs,ts,tsx,mts,cts}"
---

# React Instructions

> These instructions target React code — component purity, Hooks, state design, effects, list rendering, and DOM access.

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content`. Treat any reported
violation as blocking — fix it before reporting the work as done.

## Rules

### Component & Hook Purity

- [INST0001] **Do** keep components and custom Hooks pure — given the same props, state, and context a component must return the same JSX; run side effects in event handlers or `useEffect`, never during render.
- [INST0002] **Do** treat props and state as read-only snapshots — never mutate them directly; use setter functions and create new objects or arrays when updating.
- [INST0003] **Do** treat Hook return values and arguments as immutable — don't modify a value after it is returned from a Hook or before it is passed to one.
- [INST0004] **Do** call Hooks only at the top level of a function component or custom Hook — never inside loops, conditions, nested functions, or `try`/`catch`/`finally` blocks so that React can preserve Hook state between renders.
- [INST0005] **Do** call Hooks only from React function components or custom Hooks — never from regular JavaScript functions or class components.
- [INST0006] **Do** render components through JSX (`<Component />`) — never call a component function directly (e.g., `Component()`) because React cannot track its Hooks or lifecycle.
- [INST0007] **Don't** pass Hooks as regular values, use higher-order hooks, or dynamically mutate a Hook — Hooks must always be called directly and statically.

### State Design

- [INST0008] **Do** derive computed values during render from existing props and state instead of syncing them with `useEffect` and a state setter — eliminates an extra render cycle and keeps logic colocated.
- [INST0009] **Do** use `useMemo` to cache expensive render-time calculations rather than `useEffect` with a state setter — avoids the unnecessary render caused by a second state update.
- [INST0010] **Do** use the `key` prop to reset a component's internal state when a meaningful prop changes, instead of a `useEffect` that watches the prop and calls a setter.
- [INST0011] **Do** place event-specific logic (form submissions, clicks, navigation) in event handlers, not in Effects — Effects are for synchronizing with external systems, not for reacting to user actions.
- [INST0012] **Do** group related state variables that always change together into a single state variable or object — reduces the chance of forgetting to update one of them.
- [INST0013] **Do** model state to avoid impossible combinations — prefer a single status variable (e.g., `'idle' | 'loading' | 'error'`) over multiple independent booleans that can contradict each other.
- [INST0014] **Do** store IDs or indexes in state instead of duplicated objects — derive the full object from the source collection during render to keep data in sync.
- [INST0015] **Do** flatten deeply nested state into normalized (flat) structures that use ID references — nested state is hard to update immutably and easy to desynchronize.
- [INST0016] **Do** lift shared state to the closest common parent component — maintain a single source of truth for each piece of state rather than syncing duplicate state across siblings.

### Effects

- [INST0017] **Do** return a cleanup function from every `useEffect` that allocates resources — tear down subscriptions, timers, or connections to prevent leaks and stale callbacks.
- [INST0018] **Do** include every reactive value (props, state, values derived from them) used inside `useEffect` in its dependency array — never suppress the `react-hooks/exhaustive-deps` lint rule.
- [INST0019] **Do** create objects and functions inside `useEffect` when they are only used by that Effect — avoids adding them to the dependency array and triggering unnecessary re-runs.
- [INST0020] **Do** use state updater functions (e.g., `setCount(prev => prev + 1)`) inside Effects and callbacks instead of reading state directly — removes the state variable from the dependency array.

### Lists and Keys

- [INST0021] **Do** provide a stable, unique `key` from your data (e.g., a database ID) for every element rendered in a list — keys must be unique among siblings and stable across re-renders.
- [INST0022] **Don't** use array index as a `key` when items can be reordered, inserted, or deleted — index keys cause incorrect state mapping and subtle rendering bugs.
- [INST0023] **Don't** generate keys at render time (e.g., `Math.random()` or `crypto.randomUUID()`) — unstable keys force React to recreate the DOM and lose component state on every render.

### Custom Hooks and Refs

- [INST0024] **Do** prefix custom Hook names with `use` followed by a capital letter (e.g., `useOnlineStatus`) — this naming convention enables the linter to enforce the Rules of Hooks inside the Hook.
- [INST0025] **Do** extract repeated `useEffect` logic into custom Hooks to communicate intent, make data flow explicit, and simplify future migration when React provides better built-in APIs.
- [INST0026] **Do** use refs for non-destructive DOM operations only (focus, scroll, measure) — avoid modifying, adding children to, or removing children from DOM nodes that React manages, as it causes inconsistent state and crashes.
