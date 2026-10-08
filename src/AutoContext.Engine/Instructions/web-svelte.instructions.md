---
name: "web-svelte (v1.0.0)"
description: "Apply when writing or reviewing Svelte or SvelteKit apps (Svelte 5 runes, components, stores, routing, form actions, load)."
applyTo: "**/*.{svelte,js,jsx,mjs,cjs,ts,tsx,mts,cts}"
---

# Svelte Instructions

> These instructions target Svelte 5 and SvelteKit applications — runes-based reactivity, component design, stores, routing, and data loading.

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content` and the file's absolute
path as `filePath`. Treat any reported violation as blocking — fix it
before reporting the work as done.

## Rules

### Reactivity (Svelte 5 Runes)

- [INST0001] **Do** declare reactive component state with `$state()` — it replaces the legacy `let x` reactive declaration from Svelte 4 and makes reactivity explicit, predictable, and compatible with class fields.
- [INST0002] **Do** derive computed values with `$derived()` — derived state re-evaluates automatically when its dependencies change and is the direct replacement for `$: derivedValue = expr` reactive statements.
- [INST0003] **Do** use `$effect()` for side effects that must synchronize with state changes (e.g., updating a canvas, calling a DOM API, logging) — reactive effects replace `$: { sideEffect() }` blocks and run after the DOM has been updated.
- [INST0004] **Do** declare component props with `$props()` — `const { name, count = 0 } = $props()` replaces `export let` declarations and supports default values and TypeScript destructuring natively.
- [INST0005] **Don't** use Svelte 4 reactive syntax (`$:`, `export let`) in new Svelte 5 components — mixing legacy and rune-based reactivity in the same component leads to confusing precedence and will be removed in a future version.

### Component Design

- [INST0006] **Do** keep each component in its own `.svelte` file and co-locate its styles in the `<style>` block — Svelte scopes component styles automatically, so there is no need for CSS Modules or BEM naming inside a single component.
- [INST0007] **Do** use TypeScript inside `<script lang="ts">` for all non-trivial components — typed props, events, and stores catch mismatches at compile time; Svelte's compiler fully supports TypeScript in script blocks.
- [INST0008] **Do** use snippets (`{#snippet name()}…{/snippet}` and `{@render name()}`) to reuse markup fragments within a component — snippets replace slots for internal code reuse and avoid creating a separate child component for purely structural variations.
- [INST0009] **Do** forward DOM events and additional attributes to the underlying element using the spread operator (`<input {...attrs} />`) when building wrapper components — this avoids manually re-declaring every native attribute and event.
- [INST0010] **Don't** use Svelte 4 slot syntax (`<slot />`, `$$slots`) in new Svelte 5 components — snippets + `{@render children?.()}` are the replacements; slot syntax is deprecated in Svelte 5.

### Stores

- [INST0011] **Do** use Svelte stores (`writable`, `readable`, `derived` from `svelte/store`) for state that is shared across multiple components or needs to outlive a component's lifetime — prefer stores over passing deeply nested props for cross-cutting concerns.
- [INST0012] **Do** subscribe to stores with the `$store` auto-subscription shorthand in `.svelte` templates — the compiler automatically subscribes on mount and unsubscribes on destroy, preventing memory leaks.
- [INST0013] **Do** unsubscribe from stores manually in plain TypeScript/JavaScript files (non-`.svelte`) by storing and calling the unsubscribe function returned by `store.subscribe()` — the auto-subscription shorthand is only available inside Svelte component script blocks.
- [INST0014] **Don't** put large amounts of derived or computed state in a store when it can be expressed as a `$derived()` rune inside the component — stores carry subscription overhead; use them for shared/persistent state, not for local derivations.

### SvelteKit: Routing & Loading

- [INST0015] **Do** use `+page.server.ts` load functions to fetch data on the server and return it as `PageData` — server loads run only on the server, have access to cookies and private environment variables, and are never bundled into the client.
- [INST0016] **Do** use `+page.ts` load functions (universal loads) when the data can be fetched on both server and client — universal loads re-run on client-side navigation, enabling seamless hydration without an extra round-trip.
- [INST0017] **Do** use form actions (`+page.server.ts` `actions` export) for data mutations (create, update, delete) — actions integrate with SvelteKit's progressive enhancement via `use:enhance` and work without JavaScript enabled.
- [INST0018] **Do** use `error()` and `redirect()` helpers from `@sveltejs/kit` inside load functions and actions — throwing these helpers integrates with SvelteKit's error page handling and avoids silent failures from returning raw objects.
- [INST0019] **Don't** fetch data inside `onMount` when a load function can provide it — `onMount` runs after hydration, causing a flash of empty content; load functions provide data before the page renders.

### Performance

- [INST0020] **Do** use `{#key expr}…{/key}` blocks to force re-creation of a component or subtree when a value changes — this is the idiomatic way to reset internal component state (e.g., animations, form inputs) without keeping redundant state in the parent.
- [INST0021] **Do** use `<svelte:component this={Component} />` only when the component type is truly dynamic at runtime — static component references should be used directly to allow the compiler to optimize the output.
