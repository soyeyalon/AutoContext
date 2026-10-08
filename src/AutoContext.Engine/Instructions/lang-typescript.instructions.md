---
name: "lang-typescript (v1.0.0)"
description: "Apply when writing or reviewing TypeScript code (types, generics, utility types, narrowing, TypeScript-specific patterns)."
applyTo: "**/*.{ts,tsx,mts,cts}"
---

# TypeScript Instructions

> These instructions cover TypeScript-specific patterns — type safety, generics, utility types, and common anti-patterns. JavaScript guidelines also apply to TypeScript files.

## MCP Tool Validation

After editing or generating any TypeScript or JavaScript source file,
call the `analyze_typescript_code_style` MCP tool on the changed
source. Pass the file contents as `content`. Treat any reported
violation as blocking — fix it before reporting the work as done.

## Rules

### Configuration & Safety

- [INST0001] **Do** enable `strict: true` in `tsconfig.json` — activates `strictNullChecks`, `noImplicitAny`, `strictFunctionTypes`, and several other checks in one flag; do not patch individual strict flags instead.
- [INST0002] **Do** use `unknown` instead of `any` for values whose type is not yet determined — `unknown` forces a type check before use; `any` silently disables type checking for that value and everything it touches.
- [INST0003] **Do** enable `noUncheckedIndexedAccess` in `tsconfig.json` on new projects — it makes array index access (`arr[i]`) and record lookup (`record[key]`) return `T | undefined` instead of `T`, closing the gap between the type system and runtime reality; note it is not included in `strict: true` and is noisy to retrofit onto existing codebases.
- [INST0004] **Don't** use `// @ts-ignore` — use `// @ts-expect-error` with a brief explanation instead; it produces a compile error when the suppression is no longer needed, preventing stale suppressions.
- [INST0005] **Don't** use `any` — it disables type checking entirely for that value; use `unknown` for untyped inputs, proper types for known shapes, or generics for parameterized behaviour.

### Types & Generics

- [INST0006] **Do** add explicit return type annotations to exported functions and methods — prevents type widening, documents intent, and catches code paths that accidentally return `undefined`.
- [INST0007] **Do** use utility types (`Partial`, `Required`, `Pick`, `Omit`, `Record`, `Readonly`, `ReturnType`, `Parameters`, `Awaited`) to derive types from existing ones rather than writing duplicate type definitions.
- [INST0008] **Do** use discriminated unions with a shared literal discriminant field (e.g., `kind`, `type`, `tag`) to model distinct states — enables exhaustive narrowing in `switch`/`if` chains and prevents impossible state combinations.
- [INST0009] **Do** constrain generic type parameters with `extends` — avoids implicitly `any` type params and gives callers better inference (e.g., `<T extends object>`, `<T extends string | number>`).
- [INST0010] **Do** prefer `interface` for object shapes intended for extension or declaration merging; use `type` aliases for unions, intersections, mapped types, conditional types, and primitive aliases.
- [INST0011] **Don't** use `enum` — prefer a `const` object with `as const` and a derived `typeof` union for enumerated string values; native enums generate runtime JavaScript, have surprising numeric reverse-mapping, and don't tree-shake cleanly.
- [INST0012] **Don't** use `Function` (capital F), `Object` (capital O), or `{}` as types — they accept too broadly; use specific function signatures, `object` (lowercase), or `Record<string, unknown>` instead.

### Advanced Patterns

- [INST0013] **Do** use `readonly` on properties and `ReadonlyArray<T>` (or `readonly T[]`) for data that should not be mutated after construction — communicates intent and prevents accidental reassignment.
- [INST0014] **Do** use `never` as an exhaustiveness check at the end of `switch` or `if-else` chains over discriminated unions — assigning an unhandled case to `never` causes a compile error when a new union member is added later.
- [INST0015] **Do** use `satisfies` (TS 4.9+) to validate an expression against a type while preserving its most specific inferred literal type — useful for typed config objects where you want both type safety and autocomplete on literal values.
- [INST0016] **Do** use `as const` on constant arrays and objects to preserve literal types — without it, TypeScript widens `'admin'` to `string`, losing the ability to derive precise union types or use the values in type positions.
- [INST0017] **Do** give type guard functions a `value is T` return type annotation instead of `boolean` — without the predicate form, TypeScript does not narrow the type at the call site even when the check is correct.
- [INST0018] **Don't** use type assertions (`as SomeType`) to silence compiler errors — narrow properly with `typeof`, `instanceof`, `in`, or user-defined type guards; reserve `as` only when you have verified the type through other means and add a comment explaining why.
- [INST0019] **Don't** use non-null assertions (`!`) without proof — only apply them when you've verified by other means that the value cannot be `null` or `undefined`, and add a comment explaining why.

### File Organization

- [INST0020] **Do** give each exported `interface` or `type` alias its own file when it represents a standalone concept — mirrors one-type-per-file conventions, keeps modules focused, and makes types easy to locate by filename; a supporting type that exists only to type a field or parameter of a single parent type may stay in the parent type's file (e.g., a string-union `Kind` type alongside the interface whose `kind` field it describes).
- [INST0021] **Do** keep a `const`-as-`enum` pattern (e.g., `export const Foo = { ... } as const` and its companion `export type Foo = ...`) together with related const objects that share the same pattern in a single file — they form one logical unit; other types that happen to be co-located with const objects but represent independent concepts should still get their own file.
- [INST0022] **Do** allow barrel re-export files (`index.ts`) that aggregate and re-export from submodules — these are organizational and do not violate one-type-per-file.
- [INST0023] **Do** place files that export only `interface` and `type` alias declarations in a `src/types/` subdirectory — this clearly separates pure type definitions from runtime code and keeps the module root focused on behaviour rather than the type vocabulary; any file that contains runtime code (functions, classes, or `const` objects) stays in `src/` regardless of whether it also exports types.
