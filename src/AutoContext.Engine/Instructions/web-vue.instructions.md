---
name: "web-vue (v1.0.0)"
description: "Apply when writing or reviewing Vue.js apps (Composition API, reactivity, components, props/emits, composables, Pinia)."
applyTo: "**/*.{vue,js,jsx,mjs,cjs,ts,tsx,mts,cts}"
---

# Vue.js Instructions

> These instructions target Vue 3 applications — Composition API, reactivity, component design, state management, routing, and template practices.

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content`. Treat any reported
violation as blocking — fix it before reporting the work as done.

## Rules

### Composition API

- [INST0001] **Do** use `<script setup>` as the default authoring format for Single-File Components — it reduces boilerplate by eliminating the explicit `setup()` return, automatically exposes bindings to the template, and enables better TypeScript type inference.
- [INST0002] **Do** use `ref()` for primitive values and `reactive()` for objects that are never reassigned — `ref()` with `.value` access is the safer default because reassigning a `reactive()` variable loses reactivity silently.
- [INST0003] **Do** use `computed()` to derive values from reactive state — computed refs are cached and only re-evaluated when their dependencies change; avoid recalculating derived values inside templates or watchers.
- [INST0004] **Do** use `watch()` or `watchEffect()` only for side effects that must react to state changes (e.g., fetching data when a route param changes, persisting to `localStorage`) — never use a watcher to set another ref that could be expressed as a `computed()`.
- [INST0005] **Do** extract reusable stateful logic into composable functions (`use*.ts`) that return reactive state and methods — composables are the Vue 3 replacement for mixins and keep logic testable and composable without component inheritance.
- [INST0006] **Don't** use the Options API (`data()`, `methods`, `computed`, `watch` properties) in new components — the Composition API with `<script setup>` offers better TypeScript support, more flexible code organization, and superior tree-shaking.

### Component Design

- [INST0007] **Do** use multi-word component names (e.g., `UserProfile`, `TodoItem`) to avoid conflicts with current and future HTML elements — single-word names like `<Header>` or `<Footer>` can collide with native HTML tags or Web Components.
- [INST0008] **Do** define props with `defineProps<T>()` using a TypeScript interface — type-based declarations provide compile-time validation and are more expressive than the runtime `props` object syntax.
- [INST0009] **Do** define emits with `defineEmits<T>()` using a TypeScript call-signature interface — typed emits catch payload mismatches at compile time and document the component's event contract.
- [INST0010] **Do** use `v-bind` shorthand (`:prop`) and `v-on` shorthand (`@event`) consistently — mixing shorthand and longhand in the same template harms readability.
- [INST0011] **Do** always include a `:key` with a stable unique identifier on `v-for` — without a key, Vue's diffing algorithm reuses DOM nodes by index, which causes stale state bugs with stateful child components or form inputs.
- [INST0012] **Don't** mutate props — Vue enforces one-way data flow; emit an event to notify the parent, or use a local ref initialized from the prop if the component needs a mutable copy.

### Templates & Directives

- [INST0013] **Do** keep template expressions simple — move any logic beyond a single method call or ternary into a `computed()` or a helper function in `<script setup>`.
- [INST0014] **Do** use `v-show` for elements that toggle frequently and `v-if` for elements that are rarely shown — `v-show` keeps the element in the DOM and only toggles CSS `display`, while `v-if` destroys and recreates the subtree.
- [INST0015] **Don't** use `v-if` and `v-for` on the same element — `v-if` takes priority and cannot access the `v-for` scope variable; wrap the `v-for` in a `<template>` and apply `v-if` on the inner element, or filter the list in a `computed()` instead.

### State Management (Pinia)

- [INST0016] **Do** use Pinia as the state management library — it is the officially recommended store for Vue 3, offers first-class TypeScript support, devtools integration, and a simpler API than Vuex.
- [INST0017] **Do** define stores using the Composition API syntax (`defineStore('id', () => { ... })`) — it mirrors `<script setup>` patterns, allows full use of `ref()`, `computed()`, and `watch()`, and avoids the mutations/actions/getters ceremony.
- [INST0018] **Do** access store state via `storeToRefs()` when destructuring — plain destructuring loses reactivity; `storeToRefs()` preserves it while keeping actions as plain functions.
- [INST0019] **Don't** mutate store state directly from components outside of store actions — centralizing mutations in the store makes state changes traceable in Vue Devtools and easier to debug.

### Routing (Vue Router)

- [INST0020] **Do** lazy-load route components with dynamic imports (`() => import('./views/MyView.vue')`) — each route becomes a separate chunk that is only downloaded when the user navigates to it, reducing the initial bundle size.
- [INST0021] **Do** use `useRoute()` and `useRouter()` composables inside `<script setup>` instead of `this.$route` / `this.$router` — the composables are type-safe, work with the Composition API, and do not depend on the component instance.
- [INST0022] **Do** use navigation guards (`beforeEach`, `beforeEnter`) for authentication and authorization checks — guards run before the route renders, preventing flash-of-unauthorized-content.
- [INST0023] **Don't** manipulate `window.location` or `window.history` directly — always navigate through Vue Router to preserve SPA state, navigation guards, and browser history integration.

### Performance

- [INST0024] **Do** use `defineAsyncComponent()` to lazy-load heavy or rarely used components — the component's code is downloaded only when it is first rendered, keeping the main bundle small.
- [INST0025] **Do** use `shallowRef()` or `shallowReactive()` for large data structures that are replaced wholesale rather than deeply mutated — shallow reactivity avoids the overhead of recursively converting every nested property.
- [INST0026] **Don't** create deeply nested reactive objects when only top-level properties change — deep reactivity tracks every nested property access, which adds overhead proportional to the object depth.
