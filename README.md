![AutoContext](logo.png)

# AutoContext

AutoContext gives AI coding agents the right context for your codebase:
curated coding guidelines that shape the agent's answers, and quality
checks it can run to verify its own work. Both are filtered to the workspace,
so a project only sees what applies to it.

For installing and using the VS Code extension, see the
[extension README](src/AutoContext.VsCode/README.md).

> **Work in Progress** — Guidance and checks are refined iteratively.
> Coverage and behaviour will continue to evolve.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- [Node.js](https://nodejs.org/) 18+

## Architecture

All AutoContext state — configuration, guidance, workspace detection, tools,
workers, and logs — is owned by a single process, the **engine**. Hosts do not
read that state themselves; they connect to the engine and ask.

See [docs/architecture.md](docs/architecture.md) for the boundaries between
engine, clients, and workers, the lifecycle and state models, and the
invariants the system is built on.

## Repository structure

```text
build.ps1            Build orchestrator — the compile + format + test gate
scripts/             Focused wrappers: compile, test, format, package, publish, tag
version.json         Canonical version (single source of truth)
docs/                Architecture and design documents
src/                 Source projects
tests/               .NET test projects (TypeScript tests live beside their sources)
```

| Project | Role |
|---|---|
| `AutoContext.Framework.Pipes` | Named-pipe transport primitives |
| `AutoContext.Engine.Protocol` | Wire contract: message DTOs and address composition |
| `AutoContext.Engine.Core` | The engine as a library |
| `AutoContext.Engine` | Engine binary host (`autocontext-engine`) |
| `AutoContext.Client.Core` | Engine-dialling client library |
| `AutoContext.Workers.Core` | Worker-side runtime and task contract |
| `AutoContext.Instructions.Parser` | Instruction-file parser |
| `AutoContext.Instructions.Manifest.Generator` | Build-time generator (`instructions-manifest-gen`) |
| `AutoContext.Workers.Manifest.Generator` | Build-time generator (`workers-manifest-gen`) |
| `AutoContext.Nodejs.Core` | TypeScript substrate: pipes, logging, engine client |
| `AutoContext.Worker.DotNet` | Worker — C# and NuGet analysis |
| `AutoContext.Worker.Workspace` | Worker — Git and EditorConfig |
| `AutoContext.Worker.Web` | Worker — TypeScript analysis (Node.js) |
| `AutoContext.VsCode` | VS Code extension |

A project is a worker when it carries an `.autocontext-worker.json`
descriptor — that file, not the `AutoContext.Worker.*` name, is what the
build discovers.

## Building and testing

`build.ps1` is the gate: it compiles both stacks, verifies .NET formatting,
and runs the unit tests. A green run is the bar for "done".

```powershell
./build.ps1                  # everything — the gate
./build.ps1 TS               # TypeScript only
./build.ps1 DotNet           # .NET only
./build.ps1 -Clean           # remove build artifacts
```

While iterating, the wrappers in `scripts/` are faster because they skip the
full gate:

```powershell
./scripts/compile.ps1 DotNet     # compile only
./scripts/test.ps1 TS            # compile + test one stack
./scripts/test.ps1 -NoCompile    # test an already-compiled tree
./scripts/format.ps1             # .NET format check
```

Run `./build.ps1 -Help` for the full target and switch list. After changing
anything under `scripts/` or `build.ps1`, run `./scripts/build.tests.ps1`.

### Extension smoke tests

Launch a real VS Code instance, load the extension, and verify activation:

```powershell
./scripts/test.ps1 -Smoke
```

VS Code is downloaded on first run and cached under
`src/AutoContext.VsCode/.vscode-test/`.

## Packaging and publishing

**Packaging** builds a self-contained `.vsix` locally. **Publishing** uploads
it to the marketplaces. You don't need to package first — `publish.ps1` builds
its own package.

```powershell
./scripts/package.ps1                            # current platform
./scripts/package.ps1 All                        # all 9 platforms
./scripts/package.ps1 -RuntimeIdentifier win-x64
./scripts/package.ps1 -Local                     # runnable F5 layout, no .vsix
```

Targets: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-arm`,
`linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64`.

`-Local` stages a tree you can run directly with VS Code's F5 (Extension
Development Host) instead of producing a `.vsix`.

```powershell
./scripts/publish.ps1        # current platform
./scripts/publish.ps1 All    # all 9 platforms
```

Publishing needs two access tokens as environment variables:

| Variable | Source |
|---|---|
| `VSCE_PAT` | [Azure DevOps](https://dev.azure.com/_usersSettings/tokens) |
| `OVSX_PAT` | [Open VSX](https://open-vsx.org/user-settings/tokens) |

## Releasing

`tag.ps1` bumps every version number, compiles, tests, commits, and creates an
annotated tag:

```powershell
./scripts/tag.ps1 0.6.0           # bump, verify, commit, tag
./scripts/tag.ps1 0.6.0 -WhatIf   # dry run
```

It validates the version, refuses to go backwards, requires a clean working
tree, and only tags if the whole solution compiles and passes. Then push:

```powershell
git push origin main --follow-tags
```

## Using AutoContext from another MCP client

The tools are served over MCP, so any MCP client can consume them. Run the
engine in its MCP-server role and point the client at it:

```text
autocontext-engine --workspace <path> --mcp-server with-stdio
```

It speaks MCP over stdin/stdout, reads workspace state per request, and starts
workers on demand — no separate setup.

## License

Copyright 2026 Eyal Alon. AutoContext is licensed under the
[Apache License 2.0](LICENSE). You can use, modify, and distribute it,
including commercially, at no cost.

Use of the AutoContext name and logo is subject to [TRADEMARKS.md](TRADEMARKS.md).

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for how to
get started.
