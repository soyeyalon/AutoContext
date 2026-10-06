# AutoContext Repository Instructions

> **AutoContext repo, AutoContext extension** — this repository builds and ships the AutoContext VS Code extension itself. The instructions below govern work inside this workspace; they take precedence over any installed-extension copies of the same instruction files.

## Instruction Precedence

- **Do** treat the instruction files inside `src/AutoContext.Engine/Instructions/` as the **authoritative source**.
- **Do** prefer the in-repo files over any instructions injected by an installed AutoContext VS Code extension (e.g. `c:\Users\<user>\.vscode\extensions\alonsoft.autocontext-*\instructions\`) — those are the versions being actively edited and shipped from this workspace.
- **Don't** follow installed-extension instructions when they disagree with the in-repo copies.

## Build & Test

- **Do** route all compilation, testing, linting, and packaging through the AutoContext build tooling — never bare `npx vitest`, `npx tsc`, `dotnet build`, `dotnet test`, etc. The tooling configures paths, aliases, manifests, npm-install gating, and compilation order that bare invocations miss.
- **Do** use the orchestrator `build.ps1` from the repository root as the canonical entry point; consult the **Build Commands** table below for the command for each task.
- **Do** run `.\build.ps1 -Help` for the targets and switches (`TS`, `DotNet`, `-Clean`, `-WhatIf`). The `scripts/*.ps1` wrappers do **not** take `-Help`; read the `param()` block for their options (`-Local`, `-RuntimeIdentifier`, `All`, `-NoCompile`, `-Smoke`).
- **Don't** invoke `npx vitest`, `npx tsc`, `dotnet build`, `dotnet test`, or any other build/test tool directly — go through `build.ps1` or a `scripts/*.ps1` wrapper.

### Two-tier workflow

Shared build logic lives in `scripts/AutoContext.Build.psm1`. The root `build.ps1` script is the top-level orchestrator, and the `scripts/*.ps1` files are focused wrappers over the same module functions.

Throughout this section, **gate** means a required pass/fail quality checkpoint for the selected scope. `build.ps1` is the authoritative gate: if any required check for that scope fails, the whole run fails.

Use the right tier for the job:

- **Inner loop — fast and focused:** use the granular `scripts/*.ps1` wrappers while iterating. For example, use `scripts/compile.ps1 DotNet` for compile-only .NET work, `scripts/test.ps1 TS` for TypeScript tests, `scripts/format.ps1` for formatting, or `scripts/clean.ps1` for cleanup. These wrappers deliberately avoid the full composite gate so they return quickly. In particular, `scripts/compile.ps1` is compile-only and is not equivalent to `build.ps1`.
- **Gate — full and authoritative:** before declaring work done or proposing a commit, run `.\build.ps1`. With no arguments, it covers both stacks. For TypeScript, it compiles and runs tests. For .NET, it compiles, verifies formatting, and runs tests. A green `build.ps1` run — not just a green inner-loop wrapper — is the bar for “done”.
- **Testing behavior:** `scripts/test.ps1` compiles the selected stack before running unit tests by default, so inner-loop test runs do not use stale output. Pass `-NoCompile` to test the existing build, for example immediately after `scripts/compile.ps1` or `build.ps1`.
- **TypeScript dependency restore:** npm installs are hash-gated on `package-lock.json`, so repeated TypeScript compiles skip `npm install` when dependencies are unchanged.
- **Build-script changes:** after changing `build.ps1`, `scripts/AutoContext.Build.psm1`, or any wrapper, run `scripts/build.tests.ps1`. It exercises every action, target, and switch combination, including the wrappers, under `-WhatIf`.

### Build Commands

| Command                                | Task                                            |
|----------------------------------------|-------------------------------------------------|
| `.\build.ps1`                          | Compile everything + run unit tests (the gate)  |
| `.\build.ps1 TS`                       | Compile TypeScript + TS tests                   |
| `.\build.ps1 DotNet`                   | Compile .NET + .NET tests                       |
| `.\build.ps1 -Clean`                   | Clean all build artifacts                       |
| `.\build.ps1 -Clean All`               | Clean then run the gate                         |
| `.\scripts\compile.ps1 [TS\|DotNet]`   | Inner-loop compile (compile only, no tests)     |
| `.\scripts\test.ps1 [TS\|DotNet]`      | Inner-loop compile + tests                      |
| `.\scripts\test.ps1 -NoCompile`        | Inner-loop tests only (skip the compile)        |
| `.\scripts\test.ps1 -Smoke [DotNet]`   | Smoke-test the extension / .NET                 |
| `.\scripts\format.ps1`                 | Inner-loop .NET format gate                     |
| `.\scripts\prepare.ps1`                | Prepare (clean + compile + test + copy assets)  |
| `.\scripts\package.ps1`                | Package                                         |
| `.\scripts\publish.ps1`                | Publish                                         |
| `.\scripts\tag.ps1 <version>`          | Tag a release                                   |
| `.\scripts\build.tests.ps1`            | Run the build self-test suite                   |

> `build.ps1` is the compile + format + unit-test gate; it always runs unit
> tests. There is no standalone `Test` action — `build.ps1` tests always run
> with a fresh compile. For a tests-only run against an already-compiled tree,
> use `scripts/test.ps1 -NoCompile`. Packaging, publishing, tagging, and the
> staged `Prepare` layout live only in the `scripts/*.ps1` wrappers.

## MCP Tool Scope

Some AutoContext MCP tools are trained against production-code patterns and produce noisy or actively wrong findings when applied to test code (where the testing-specific instructions — `testing`, `dotnet-testing`, `dotnet-xunit`, `web-vitest`, etc. — apply instead).

- **Don't** invoke the C# analyzers (`analyze_csharp_*`) on anything under `tests/**` — that subtree holds all C# test code, including dedicated `<Project>.Tests.Support` projects and `Support/` folders inside `<Project>.Tests` projects. Validate those against the matching testing instructions (`testing`, `dotnet-testing`, `dotnet-xunit`) instead.
- **Don't** invoke the TypeScript analyzers (`analyze_typescript_*`) on anything under `src/AutoContext.*/tests/**` — that subtree holds all TypeScript test code and test support. Validate those against the matching testing instructions (`testing`, `web-testing`, `web-vitest`, `web-mocha`, `web-playwright`) instead.
- **Don't** assume new production-code-shaped MCP analyzers shipped from this repo are safe to run on test code. Apply the same scope rule, and when in doubt, read the tool's `description` in `src/AutoContext.Engine/Resources/mcp-tools-registry.json` to confirm scope before invoking.

## Versioning

- **Don't** modify version numbers anywhere in the codebase without explicit user permission. This includes (but is not limited to) `version.json`, `package.json` `version` fields, `.csproj` `<Version>` / `<VersionPrefix>` properties, instruction-file frontmatter `name: "<id> (vX.Y.Z)"` strings, and any other semver string baked into source.
- **Do** treat version bumps as a deliberate, user-driven action performed via `scripts/versionize.ps1` — never opportunistically as part of an unrelated change.
