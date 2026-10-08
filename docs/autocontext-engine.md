# Plan: `autocontext-engine` (per-workspace state owner and central process)

## Motivation

Today AutoContext spreads its state across two homes:

1. **`AutoContext.Mcp.Server`** is the MCP-protocol facade Copilot talks
   to. Internally it is already a pipe orchestrator — it dispatches to
   workers over named pipes and dials four sideband pipes back to the
   VS Code extension (`log`, `health-monitor`, `worker-control`,
   `extension-config`) to pick up state the extension owns.
2. **The VS Code extension** owns the live state — `.autocontext.json`,
   the curated instructions corpus, projection (file-level and
   rule-level disable), workspace detection, override resolution — and
   re-projects it into static artefacts (`instructions/.generated/`,
   per-window staging dirs) and pushes it to the orchestrator over the
   four sideband pipes.

That topology made sense when VS Code was the only host. It does not
scale. The Anthropic Claude plugin needs the same view — same config,
same projected instructions, same disable-rule semantics — and there
is no longer a single host that can act as the source of truth for
the others. Running the projection logic a second time inside the
plugin would fork the source of truth; a SessionStart hook reading
disk artefacts the extension wrote would couple the plugin to one
specific host being installed.

The fix: pull the state out of the VS Code extension and into a single
.NET process that **every host is a client of**. That process is
`autocontext-engine`.

## Topology — motivating clients

The engine is a pipe-RPC server. Any process that can speak the wire
protocol is a client; the engine does not maintain a closed list of
blessed clients, and nothing in this document treats the set as
closed. The two clients enumerated below — the **VS Code extension**
and the **agent plugin (hooks)** — appear here because they
*motivate* engine-side design decisions (the `Agent.*` RPC family,
the discriminated-envelope shape consumed by the tree views, the
four-pipe split that lets a forgotten log tail not pin the engine
alive, …). Any other client — a debug CLI, a status probe, an
ad-hoc script piping JSON-RPC into `nc` — is just another consumer
of the same surface and is documented in its own design doc rather
than here.

```
                   .autocontext.json   instructions/   workspace files
                            \              |              /
                             \             |             /
                            +-------------------------------+
                            |      autocontext-engine       |
                            |   (one process per workspace) |
                            |                               |
                            |  Config · Instructions ·      |
                            |  Workspace · MCP Tools ·      |
                            |  Worker dispatch ·            |
                            |  pipe RPC (daemon role)       |
                            +-------------------------------+
                              ^         ^         ^
                              |         |         |
                              |         |         +--- AutoContext.Worker.* (spawned)
                              |         |
                              |         +--- Agent plugin (hooks)
                              |              (consumer; runs under any hook host —
                              |               Claude Code, VS Code Copilot, …)
                              |
                              +--- VS Code extension
                                   (UI surface; toggles files & rules)
```

Two motivating clients, two jobs:

- **VS Code extension** is the **UI surface** for the engine. The user
  toggles instruction files on/off, disables individual rules, exports
  workspace overrides, and watches state in the Instructions /
  MCP Tools tree views — all by issuing RPCs against the engine. The
  extension is a pure RPC consumer: it never writes a projected
  instruction file to disk. Instruction delivery to the chat surface
  is the agent plugin's job, not the extension's — even when the
  chat surface lives inside the same VS Code window.
- **Agent plugin (hooks)** is a **consumer**. The plugin ships two
  hooks today, both wired to the engine over the per-workspace pipe:
  - **`SessionStart`** — calls `Instructions.GetAlwaysAttached` and
    emits the bodies as `additionalContext` so the always-attached
    rules apply to every turn (see issue addressed in the
    `Instructions.*` RPC list below).
  - **`UserPromptSubmit`** — calls `Discovery.RouteForPrompt(prompt)`
    (see RPC surface below) and emits the result as a discovery
    preamble naming the strongly-relevant MCP tools and instruction
    files for this turn.

  Disabled state is opaque to the plugin: a disabled file is simply
  not returned, a disabled rule is simply not in the body. The plugin
  never sees `.autocontext.json`, never resolves overrides, never
  strips `[INSTxxxx]` tags. The plugin uses Anthropic's Claude Code
  hooks format on disk (`hooks/hooks.json` + `.cjs` scripts), but it
  is not Claude-only: any hook host runs it. Today that means
  **Claude Code** and **VS Code Copilot**; future hosts that read the
  same format inherit support for free.

  The remaining standard hook events (`Stop`, `SubagentStart` /
  `SubagentStop`, `PreCompact`, `PreToolUse`, `PostToolUse`) are
  not implemented today but are explicitly **in scope for the
  engine design** — each one is an agent-loop transition the
  engine cannot observe on its own, and surfacing them through the
  engine lets every client (the extension's tree views, the CLI,
  future hosts) react uniformly. Sketch of the role each plays:

  - **`SubagentStart` / `SubagentStop`** — the natural place to
    materialise instruction *files* on disk for sub-agents that
    need a `Uri` rather than inline context. The hook calls
    `Instructions.GetAlwaysAttached` (and a future task-scoped
    `Discovery.RouteForPrompt(subagentTaskPrompt)`), writes the
    bodies under
    `%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\cache\subagents\<sessionId>\`
    (POSIX equivalent), and notifies the engine via
    `Agent.SubagentStarted(sessionId, taskPrompt)`. `SubagentStop`
    deletes the directory and calls `Agent.SubagentStopped(sessionId)`.
    The engine re-broadcasts both events on `Agent.Events.Subscribe`
    so the VS Code tree view can show "active sub-agents: 2" with
    a drill-down listing each session's task and materialised file
    set — giving the user cross-host observability the chat
    surface alone does not provide.
  - **`PreCompact`** — the agent host is about to drop conversation
    history. Always-attached instructions and the static discovery
    preamble must survive compaction; the hook re-injects both
    immediately after the compact event by calling
    `Instructions.GetAlwaysAttached` again and re-emitting the
    `additionalContext` block. The hook also signals
    `Agent.Compacted(sessionId)` so the engine can mark any
    session-scoped derived state (sub-agent file caches, routing
    history) for re-evaluation.
  - **`PreToolUse`** — fires immediately before the agent invokes
    a tool. The hook calls `Discovery.RouteForTool(toolName)`
    (a `Discovery.*` extension keyed by tool identity rather than
    prompt text) and emits any tool-gated instruction file that
    `applyTo` would target — e.g. invoking `analyze_csharp_code_style`
    surfaces the C# coding-standards instructions, invoking a
    git-commit-analysis tool surfaces the commit-message
    instructions. This catches turns the prompt-text router
    misses (the user said "fix this" but the agent picked a C#
    analyzer).
  - **`PostToolUse`** — fires after a tool returns. Three roles:
    (a) when the tool wrote to `.autocontext.json` or to a file
    under `<workspace>/.github/instructions/`, the hook calls
    `Engine.Reload()` synchronously so the next turn sees the new
    state without waiting for a debounced FS-watch event;
    (b) the hook signals `Agent.ToolUsed(sessionId, toolName, outcome)`
    to the engine, which folds it into a per-session usage histogram
    available via `Diagnostics.Run` for "which tools did this
    session actually use" reports;
    (c) **verification** — when the tool wrote a workspace file, the
    hook calls `Discovery.RouteForFile(path)` for the instruction
    files whose `applyTo` covers that path and the MCP tools those
    files oblige, invokes each obliged tool through
    `McpTools.Invoke` on the file's current content, and emits any
    violations as `additionalContext` so the agent sees the report
    in the same turn it made the edit. Parameters are filled from
    the registry's declared parameter names by convention
    (`content` from the file, `filePath` from the path,
    `projectDirectory` from the nearest ancestor holding a project
    file); a tool whose required parameters the hook cannot fill is
    skipped and logged, never guessed. The hook also signals
    `Agent.FileTouched(sessionId, path)` so the engine holds the
    session's touched-file set for the `Stop` backstop. The checks
    run whether or not the agent remembered the instruction file's
    `## MCP Tool Validation` section: the section states the
    obligation, the hook discharges it.
  - **`Stop`** — fires when the agent finishes its turn. The hook
    reads `Agent.TouchedFiles(sessionId)`, re-runs the obliged tools
    over every file still in the set, and — on a host whose `Stop`
    hook can refuse the turn — returns a block decision carrying the
    aggregated report, so a turn cannot end with a known violation
    outstanding. A clean run signals `Agent.FilesVerified(sessionId)`,
    which clears the set. Hosts whose `Stop` cannot refuse still get
    the `PostToolUse` feedback; the backstop is additive. The hook
    then flushes any session-scoped client cache it owns, signals
    `Agent.TurnEnded(sessionId)` to the engine, and releases any
    keep-alive grip the hook held on the engine pipe so the
    idle-timeout countdown can begin if no other client is
    connected.

  Common shape: hook scripts stay thin composers of existing RPCs
  for *content* (`Instructions.*`, `Discovery.*`, `McpTools.*`,
  `Config.*`, `Workspace.*`); the only new RPC family these hooks
  introduce is **`Agent.*`** — a small set of fire-and-forget
  notifications turning hook events into engine-broadcast signals
  via `Agent.Events.Subscribe`. The engine itself never observes
  the agent loop; the hook is the only sensor, and `Agent.*` is
  how that sensor's reading reaches every other client. This
  preserves the rule "engine adds RPCs only when a hook needs
  state the engine does not already expose" — agent-loop
  transitions *are* such state, so they get RPCs; everything else
  composes existing surfaces.
When VS Code Copilot runs the agent-plugin hooks alongside the
AutoContext extension in the same window, the two surfaces are
**independent** clients of the same engine, not nested layers. The
hook process talks to the engine directly, not via the extension —
the extension neither launches the hook nor proxies its RPCs.

## At a glance — reference index

A one-screen catalog of every named entity in this design.
Entries are terse pointers; the authoritative definition lives in
the linked section below. New entities added to the design must
also land here so the index stays the system's table of contents.

### Binaries and processes

| Name | Kind | Scope | See |
|---|---|---|---|
| `autocontext-engine` (daemon role) | .NET binary | one process per (workspace, launcher instance); binds four pipes, owns writes, runs housekeeping | [Engine binary](#engine-binary) |
| `autocontext-engine --mcp-server with-stdio` (MCP-server-only role) | same .NET binary, different role | one process per MCP-host launch; no daemon pipes / registry entry, stdio-only, spawns workers on demand for worker-backed tools over private dispatch pipes (torn down on exit), re-reads `.autocontext.json` per request, exits on stdio EOF | [Engine binary](#engine-binary) |
| `AutoContext.Worker.DotNet` / `.Workspace` / `.Web` | .NET / Node task workers | spawned lazily by the engine via `WorkerProcessService` | [What the engine absorbs](#what-the-engine-absorbs-from-todays-topology) |
| `AutoContext.Mcp.Server` | retired in this plan | absorbed into the engine | [What the engine absorbs](#what-the-engine-absorbs-from-todays-topology) |

### Distributed bundle layout

The **shipped** shape of an engine bundle inside any host artefact
(VSIX, plugin root, GitHub-released tarball). This is the runtime
filesystem the engine resolves against via `AppContext.BaseDirectory`
— not the source-tree layout under `src/`, and not the multi-RID
build-output tree under `artifacts/engine/`. Each shipped artefact targets one
platform (one VSIX per platform via `vsce package --target <target>`,
one plugin release per platform, one GitHub-release tarball per RID),
so the per-RID segment that exists in build staging is **absent**
from the shipped product:

```
engine/
  autocontext-engine[.exe]               # engine binary
  <framework dlls / runtime files>       # self-contained .NET runtime for the engine
  Instructions/                          # curated corpus (read-only side-car)
  Resources/                             # build-generated read-only manifests
  Workers/<id>/<entrypoint>              # one self-contained subdir per worker
```

At build-output staging time the layout is
`artifacts/engine/<rid>/{autocontext-engine, runtime, Instructions/, Resources/, Workers/}`
with one subtree per supported RID (`win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, `linux-arm`, `linux-musl-x64`,
`linux-musl-arm64`, `osx-x64`, `osx-arm64`); per-platform
packaging picks the matching `<rid>/` and copies its contents into
`engine/` in the shipped artefact. Other host bundles that need an
engine copy (any client distribution that wants to cold-spawn its
own engine rather than dial an existing one) nest the same
`engine/` subtree under their own root; they are not part of this
document.

See [Distribution](#distribution) for per-file roles, manifest
shapes, and host-side resolution rules.

### Engine CLI switches

| Switch | Required | Notes |
|---|---|---|
| `--workspace <path>` | yes | absolute workspace path (P4) |
| `--instance-id <uuid>` | yes (daemon role) | launcher-minted UUIDv4 (P4); rejected in MCP-server-only role |
| `--instance-label <text>` | no | freeform observability descriptor (≤ 200 printable-ASCII) |
| `--idle-timeout <seconds>` | no | non-negative integer; default `300`; `0` disables the idle gate |
| `--parent-pid <pid>` | no | watchdog: engine self-exits when the named OS process vanishes (start-time matched to defeat pid recycling) |
| `--retention <duration>` | no | housekeeping retention window; default `1d` |
| `--log-level <level>` | no | `trace` \| `debug` \| `information` \| `warning` \| `error` \| `critical` \| `none`; minimum level a record must carry to be emitted. Omitted leaves the host's own logging configuration in force |
| `--log-rotation <size>` | no | `small` (default) — rotate at 1,000 lines OR 5 MB; `large` — rotate at 5,000 lines OR 25 MB |
| `--mcp-server <mode>` | no | `with-stdio` (only value today); selects MCP-server-only role |
| `--version` | no | RID-independent |

See [Engine options (CLI surface)](#engine-options-cli-surface).

### Pipes

Name shape: `autocontext-engine:<kind>@<workspaceHash>#<instanceId>`
where `<workspaceHash>` = `sha256(normalisedWorkspacePath):0..16`,
`<instanceId>` = launcher-minted UUIDv4. Four pipes per
(workspace, launcher instance):

| Kind | Keep-alive | Handshake | Payload | Consumer shape |
|---|---|---|---|---|
| `rpc` | yes | `Engine.Hello` required | length-prefixed JSON-RPC frames | every functional client that mutates state or reads state-bearing surfaces |
| `events` | yes | `Hello` envelope required | broadcast envelopes (`Engine.Lifecycle`, future) | every cache-invalidating client |
| `health` | no | none | one small status JSON document | spawners deciding "is the engine up?", status probers |
| `logs` | no | none | NDJSON record stream (one record per line) | log tailers (interactive or scripted) |

See [Lifecycle](#lifecycle) and [P4](#p4-workspace-identity-is-one-hash-engine-identity-adds-one-uuid).

### On-disk paths and ownership

Every path AutoContext touches has exactly one owner (P5).

| Path | Owner | Lifetime |
|---|---|---|
| `<workspace>/.autocontext.json` | engine | workspace; cross-instance shared on disk |
| `<workspace>/<root>/instructions/<name>.instructions.md` (each `<root>` from `engine.instructions.overridesRoots`, default `.github`) | user | workspace; overrides bundled |
| `<host-bundle>/engine/{autocontext-engine, Instructions/, Resources/, Workers/}` | build | read-only at runtime |
| `…\autocontext\engine-registry.json` | every live engine (co-owned) | entry-per-instance liveness registry; **append-only at startup**, own entry removed at graceful shutdown |
| `…\autocontext\<workspaceHash>\<instanceId>\logs\engine.log` | engine | rotated in-process by `--log-rotation` thresholds; rotated files retained per `--retention` |
| `…\autocontext\<workspaceHash>\<instanceId>\logs\crash.log` | engine | write-once tombstone for unhandled-exception / fail-fast exits; absent on graceful shutdown; reaped with the rest of the per-instance subtree under `--retention` |
| `…\autocontext\<workspaceHash>\<instanceId>\logs\worker-<workerId>.log` | engine | one file per spawned worker; records routed by `category` prefix; same rotation + retention rules as `engine.log` |
| `…\autocontext\<workspaceHash>\<instanceId>\cache\<client>\…` | client | client-managed |

The per-instance subtree is **nested**: `<workspaceHash>` is a directory
shared by every launcher instance that ever ran against this workspace,
and each launcher's per-spawn `<instanceId>` is a subdirectory underneath
it. Endpoint names use the flat `<workspaceHash>#<instanceId>` shape
because the OS pipe namespace is flat (P4); on-disk paths use the nested shape
because directory enumeration over a workspace's instance history is a
first-class housekeeping operation. `engine-registry.json` lives at the
autocontext cache root, **not** under either `<workspaceHash>` or
`<instanceId>` — it is the one shared file every live engine on the
machine co-owns.

`…` = `%LOCALAPPDATA%\autocontext\` on Windows, `$XDG_CACHE_HOME/autocontext/`
or `~/.cache/autocontext/` on POSIX.

See [P4](#p4-workspace-identity-is-one-hash-engine-identity-adds-one-uuid)
/ [P5](#p5-on-disk-path-ownership-is-explicit-and-exclusive).

### RPC surface

Grouped by namespace (handler families live in the engine; transports
are marshalling shims — P1).

| Namespace | Methods |
|---|---|
| `Engine.*` | `Hello`, `RegistryEntries`, `Shutdown`, `WriteLog` (fire-and-forget from workers), `Lifecycle.Subscribe` |
| `Config.*` | `Get`, `Subscribe`, `ToggleFile`, `ToggleRule` |
| `Instructions.*` | `List`, `Categories`, `Get`, `GetAll`, `GetAlwaysAttached`, `GetRaw`, `SearchContent`, `SearchByMetadata`, `Subscribe` |
| `Workspace.*` | `Detect`, `Info` |
| `Logs.*` | `GetEngine`, `TailEngine`, `GetWorker`, `TailWorker` |
| `McpTools.*` | `List`, `Invoke` (future: `InvokeStream`, `GetDescription`, `SearchByMetadata`, `SearchByContent`) |
| `Discovery.*` | `RouteForPrompt`, `RouteForTool`, `RouteForFile` |
| `Agent.*` | `SubagentStarted`, `SubagentStopped`, `Compacted`, `ToolUsed`, `TurnEnded`, `FileTouched`, `FilesVerified` (fire-and-forget notifications), `TouchedFiles` (read), `Events.Subscribe` |

State-bearing reads return discriminated envelopes
(`ok` / `disabled` / `not-found` / `*-error`) — P2.
See [RPC surface (initial)](#rpc-surface-initial).

### Wire envelopes (one-line shapes)

| Envelope | Shape |
|---|---|
| Log record (engine + worker, on `logs` pipe, in `engine.log` and `worker-<workerId>.log`) | `{ timestamp, category, level, eventId?, message, properties?, exception? }` |
| `Engine.RegistryEntries` entry | `{ workspaceHash, workspacePath, instanceId, instanceLabel, processId, processStartTimeUtc, engineVersion, startedAt, retention }` |
| `Instructions.List` row | `{ key, fileName, name, version, description, applyTo?, hasChangelog, contentHash, alwaysAttached, label?, categories[], disabled, source, overridePath?, sections? }` |
| `Instructions.Categories` response | `{ categories: [{ name, description }] }` — the curated taxonomy (bucket definitions), static for the process lifetime |
| `Instructions.Get` response | `\|` of `{ kind: "ok", … }` / `{ kind: "disabled", … }` / `{ kind: "not-found", … }` |
| `McpTools.Invoke` response | `\|` of `{ kind: "ok" \| "tool-error", content, isError? }` / `{ kind: "schema-error", errors[] }` / `{ kind: "disabled" \| "not-found" }` |
| `Workspace.Detect` | `{ flags: { hasDotNet, hasCSharp, …~60 }, extensions[] }` — no `overrides` field; that inventory is reachable via `Instructions.List` |

### Log categories (prefix taxonomy, convention not closed enum)

| Prefix | Producer |
|---|---|
| `engine.rpc.<Handler>` | engine RPC handlers |
| `engine.events` | engine `Lifecycle` / `Agent.Events` broadcast |
| `engine.health` | engine health-pipe handler |
| `engine.lifecycle` | engine startup / shutdown / idle transitions |
| `engine.startup` | argv parse, pipe bind, manifest load |
| `engine.logging` | log pipeline's own diagnostics (drops, slow-subscriber evictions) |
| `worker.<workerId>.engine.stderr` | captured worker stderr from the engine's supervision channel |
| `worker.<workerId>` | worker root (`worker.dotnet`, `worker.workspace`, `worker.web`) |
| `worker.<workerId>.<Type>` | per-type sub-category under a worker |

See [Log categories](#log-categories).

### Engine-internal services (.NET, under `AutoContext.Engine/`)

| Service | Role |
|---|---|
| `ConfigFileService` | owns `.autocontext.json`, validates and broadcasts writes |
| `InstructionsManifestService` | merged catalog + manifest snapshot — startup ingestion + per-request projection |
| `InstructionsBodyProjector` | raw → projected body (disabled-rule filter, `[INSTxxxx]` tags preserved as reference anchors, override resolution) |
| `instructions-manifest-gen` (build-time tool, not a runtime service) | reads `instructions-catalog.json` + the corpus, emits `instructions-manifest.json` |
| `workers-manifest-gen` (build-time tool, not a runtime service) | aggregates the per-worker `.autocontext-worker.json` descriptors under `src/`, emits `workers.json` |
| `InstructionsFullTextSearchService` | in-memory full-text search over instruction bodies (replaces extension-side trigram index) |
| `WorkspaceContextDetector` | workspace detection (absorbed from extension) |
| `WorkerProcessService` | lazy `ensureRunning(workerId)` worker dispatcher (absorbed from MCP server) |

### Composition seams

| Seam | Layer |
|---|---|
| `IHostApplicationBuilder.AddAutoContextEngine(Action<EngineOptions>)` | engine library's single public entry; CLI and tests both call it |
| `EngineOptions` | CLI-surfaced knobs + library-only knobs (corpus root override, endpoint override) |
| `AddEngineLoggerProvider()` (in `AutoContext.Workers.Core`) | worker-side logging seam routing `ILogger<T>` to `Engine.WriteLog` |
| `EngineDaemonManager` (TS, `Nodejs.Core/src/engine/`) | only shared TS class; owns engine-daemon lifecycle (find-or-spawn, supervise) and pipe-RPC dial for extension and hooks |

See [Composition contracts](#composition-contracts).

### Build-generated `Resources/` manifests (per-RID, read-only at runtime)

| File | Role |
|---|---|
| `instructions-catalog.json` | **hand-authored** curatorial layer: category taxonomy (`name` + `description`) and per-file `label`, category membership, and `activationFlags`. Tracked source — not generated. |
| `instructions-catalog.schema.json` | JSON-schema for the catalog (hand-edited) |
| `instructions-manifest.json` | **build-generated** per-file facts: section maps, parsed `applyTo` extension sets, `version`, `description`, `contentHash`, `hasChangelog`. Carries no body text — full-text search indexes the projected bodies at runtime. |
| `mcp-tools-catalog.json` | **hand-authored** activation + UI catalog. Answers two questions the registry deliberately does not: **when** each tool activates (category `activationFlags`, accumulated down the tree and ANDed) and **where** it appears in the UI (its category in the presentation tree). Carries no model-facing tool contract; joins the registry by tool `name` + `workerId`. Same curatorial concept as `instructions-catalog.json` (hand-authored layer over a separate facts file) but its own shape. Tracked source — not generated. |
| `mcp-tools-registry.json` | **hand-authored** execution registry. Describes **what** each tool is for the model and how it dispatches: a flat `tools[]` list, each tool `{ name, workerId (FK to workers.json), description, parameters, editorconfig? }`. The `description` and `parameters` are the model-facing contract surfaced over MCP `tools/list`; `workerId` is the source-of-truth dispatch target. No activation or UI concerns, and no nested worker/task tree. |
| `mcp-tools-registry.schema.json` | JSON-schema for the registry (hand-edited) |
| `workers.json` | build-generated worker roster, aggregated from the per-worker `.autocontext-worker.json` descriptors (`id`, `type`, optional `label`, `command`, copied verbatim) |

See [Resource manifests](#resource-manifests).

### Design principles (cross-cutting)

| Id | Rule |
|---|---|
| **P1** | One handler per capability; transports are marshalling shims |
| **P2** | Discriminated envelopes for state-bearing reads (`ok` / `disabled` / `not-found` / `*-error`) |
| **P3** | Three representations — on-disk (authoring/generation), engine-internal (runtime model), wire (per-RPC projection) — are decoupled; none dictates another's shape |
| **P4** | Workspace identity is one hash; engine identity adds one launcher UUID |
| **P5** | On-disk path ownership is explicit and exclusive |
| **P6** | Subscriptions are first-class; clients never poll or watch |
| **P7** | Two-layer matching: coarse on the producer, fine on the consumer |
| **P8** | Async I/O end-to-end; no sync-over-async, no blocking on hot paths |
| **P9** | Concurrent reads, single-writer per resource, snapshot-immutable across reloads |
| **P10** | In-process async hooks are single-subscriber; cross-process fan-out is `*.Subscribe` |

See [Design principles (cross-cutting)](#design-principles-cross-cutting).

## What the engine absorbs from today's topology

The engine is the new home for everything that today is split between
`AutoContext.Mcp.Server` and the VS Code extension's pipe-server
classes:

| Today | Lives in | Becomes |
|-------|----------|---------|
| `AutoContext.Mcp.Server` (orchestrator + MCP/stdio + worker dispatch + registry) | Standalone process | **Same `autocontext-engine` binary, MCP-server-only role** (`--mcp-server with-stdio`). Reads workspace state directly from `.autocontext.json` (re-read per MCP request) and bundled side-car corpus; binds no daemon pipes and writes no registry entry, but spawns workers on demand for worker-backed tools (`analyze_*` / `read_*`) over private dispatch pipes, torn down on process exit. Concurrent daemon-role engine on the same workspace (when launched by a different host) is the writer; MCP-server role is a read-mostly view plus on-demand worker dispatch. |
| `AutoContextConfigManager` (TS, extension) | Extension process | **Engine internal**: `ConfigFileService` (.NET) |
| `InstructionsFilesManager` + `InstructionsFileContentProjector` + `instructions-files-metadata-generator` + client-side content trigram index | Extension process | **Engine internal**: `InstructionsManifestService` + `InstructionsBodyProjector` + the build-time `instructions-manifest-gen` generator (now runs **both** at build time — reading the curated `Resources/instructions-catalog.json` and the corpus to emit the `Resources/instructions-manifest.json` side-car — **and** at engine startup, where the engine merges catalog + manifest into an immutable snapshot, applies per-request projection against workspace state, and returns rows via `Instructions.List`) + `InstructionsFullTextSearchService` (replaces the client-side trigram index; built lazily in-memory over the projected bodies `InstructionsBodyProjector` returns) |
| `servers.json` (TS-side worker/MCP-server inventory) + `mcp-workers-registry.json` (MCP-server–side worker dispatch table) | Extension `resources/` + `AutoContext.Mcp.Server/` | **Replaced** by build-generated `Resources/workers.json` (aggregated from the per-worker `.autocontext-worker.json` descriptors under `src/` — carrying that descriptor is what makes a project a worker, and its `id`, `type`, optional `label`, and `command` are copied verbatim; the `AutoContext.Worker.*` name is a convention the generator keeps only as a lint, failing the build when such a project carries no descriptor) + `Resources/mcp-tools-registry.json` (renamed from `mcp-workers-registry.json`; a hand-authored flat `tools[]` dispatch table, each tool carrying a `workerId` FK) + `Resources/mcp-tools-registry.schema.json` (its JSON-schema) + the hand-authored `Resources/mcp-tools-catalog.json` UI catalog. The old `servers.json` mixed MCP-server identity with worker identity; the MCP server is gone (consolidated into the engine), so the worker-only file is what remains. |
| `LogServer` (sideband pipe) | Extension process | **Engine internal**: the engine binds the `logs` pipe (one of the four pipes — see `### Lifecycle`) as a unified server-streaming sink that fans out engine-emitted records **and** worker-emitted records forwarded through `Engine.WriteLog`, distinguished by the `category` field. The engine also persists every record to `…\<workspaceHash>\<instanceId>\logs\engine.log` (P4 / P5); clients tail the pipe instead of inventing their own log-watcher. |
| `HealthMonitorServer` (sideband pipe) | Extension process | **Engine internal**: the engine binds the `health` pipe (one of the four pipes — see `### Lifecycle`) as a passive readiness/heartbeat probe — cheap connect-and-read, no `Engine.Hello` required, never counts toward the idle-timeout keep-alive gate. Replaces the extension-side `HealthMonitorServer` that earlier topology had clients dialling back to. |
| `WorkerControlServer` (sideband pipe) | Extension process | **Engine internal**: engine spawns workers via the same lazy gate |
| `AutoContextConfigServer` (sideband pipe) | Extension process | **Gone** — config IS engine state; pushes to subscribers via `Config.Subscribe` over the engine pipe |
| `WorkspaceContextDetector` | Extension process | **Engine internal**: workspace detection runs on engine startup; clients read via `Workspace.*` RPCs |
| `instructions/.generated/` (on-disk projection) | Extension's extensionPath | **Gone** — projection happens in-memory in the engine; the extension consumes projected bodies as strings over the pipe |
| `instructions/.workspaces/<hash>/` per-window staging | Extension's extensionPath | **Gone** — no on-disk staging needed |

Workers (`AutoContext.Worker.DotNet`, `AutoContext.Worker.Workspace`,
`AutoContext.Worker.Web`) are **not** absorbed. They stay as separate
binaries with their existing JSON-RPC-over-pipe protocol, spawned by
the engine on demand using the same lazy `ensureRunning(workerId)`
pattern `WorkerControlClient` uses today.

### What `AutoContext.Framework.*` carries over

Today's single `AutoContext.Framework` substrate project is **split
into four sibling projects** as part of this rollout, with
direction-of-flow flipped in two places where the old extension was
the server and the engine is now. Splitting now (rather than leaving
one assembly that every host references in full) buys enforced
reference asymmetry: engine and dialer libraries depend on three of
the four sub-projects, workers depend on all four, and the project
graph makes that distinction visible at the `<ProjectReference>`
level rather than as a folder convention inside one assembly. The
four sub-projects, by namespace:

- **`AutoContext.Framework.Pipes`** — pipe transport primitives.
  Reused as-is from today's `AutoContext.Framework/Pipes/`:
  `PipeListener` / `BoundPipeListener`, `PipeTransport`,
  `LengthPrefixedFrameCodec`, `PipeKeepAliveClient`, and the
  `PipeTransientExchangeClient` / `PipePersistentExchangeClient` /
  `PipeStreamingClient` triad. These are the substrate behind the
  engine's four-pipe topology (P4 — `rpc`, `events`, `health`,
  `logs`). The framing layer, ready-marker contract, and
  back-pressure discipline are all already battle-tested by the
  current MCP-server↔worker plumbing; the engine reuses them
  unchanged. `EngineDaemonManager` (the only shared TS class — see
  `## Sharing principle`) and the engine's own pipe host both sit
  on top of this project.
- **`AutoContext.Framework.Logging`** — *retired in Phase 8.* This
  project transitionally held the legacy worker→extension sideband
  sink — `LogEntry` / `JsonLogEntry` `(Category, Level, Message,
  Exception, CorrelationId)` shipped via `PipeLoggerProvider` /
  `LoggingClient` to the extension's `LogServer` — plus the
  `CorrelationScope` helper. Under the engine design that direction
  reverses: the engine binds the `logs` pipe and workers ship to it
  via `Engine.WriteLog`, through the worker-side sender
  (`AddEngineLoggerProvider` / `EngineLoggerProvider` /
  `EngineLogIngestRing` / `EngineWriteLogClient`) that lives in
  `AutoContext.Workers.Core` — because marshalling `JsonLogRecord`
  requires `Engine.Protocol`, a dependency a `Framework.*` leaf may
  not take. Phase 8 deletes the sideband and moves `CorrelationScope`
  into `AutoContext.Workers.Core` next to its only consumer, emptying
  and retiring this project. The canonical wire log envelope
  (`LogRecord` / `JsonLogRecord`) is owned by
  `AutoContext.Engine.Protocol`.
- **`AutoContext.Engine.Protocol`** — cross-side DTOs. New
  sub-project (no equivalent in today's substrate); holds the
  protocol-version integer constant that `Engine.Hello` exchanges,
  the endpoint builder (`rpc` / `events` / `health` / `logs` ×
  workspace-hash × instance-UUID — P4), the canonical
  `LogRecord` envelope shared by `Engine.WriteLog` and the `logs`
  pipe, the discriminated-union envelope base shapes (P2 — `ok` /
  `disabled` / `not-found` / `*-error`), and the per-RPC request
  / response DTOs that both engine handlers and typed dialer
  clients marshal, plus the pure address formatters
  (`ServiceAddressFormatter` and the endpoint builder) that both
  sides need to agree on a pipe name. Inert apart from that
  formatting — no I/O, no transport dependency. The
  source-generated `System.Text.Json` context for every DTO ships
  in this project.
- **`AutoContext.Workers.Core`** (renamed from the working name
  `AutoContext.Framework.Services`) — the worker-side runtime for
  **.NET** workers. It
  references `Framework.Pipes` + `Engine.Protocol`; because it
  *dials the engine*, depending on the
  `Engine.Protocol` wire contract is correct (and is why it is not a
  `Framework.*` project). `WorkerHostOptions`,
  `WorkerTaskDispatcherService`, `WorkerHostBuilderExtensions`, and
  `IMcpTask` are hosting scaffold, **not** the worker contract:
  `WorkerTaskDispatcherService` is the `BackgroundService` that binds
  a worker-side pipe and routes requests to whatever handlers the
  worker registered, `IMcpTask` is the in-process handler shape those
  registrations happen to use, and `WorkerHostBuilderExtensions` is
  the DI extension that wires both. A worker is free to satisfy the
  dispatch protocol some other way, and both alternatives exist in the
  tree today: the Node worker uses its own TypeScript task type, and
  the test-tree `AutoContext.Worker.Test.Driver` is a **.NET** worker
  that references only `Framework.Pipes` + `Engine.Protocol`,
  implements its own task interface and dispatcher, and never touches
  this project at all.
  What makes a process a worker is its descriptor plus the dispatch
  wire protocol; everything in this paragraph is a convenience for
  .NET workers that want it. `WorkerHealthMonitorService` (the
  renamed `HealthMonitorClient`) flips direction here: today it dials
  the extension's `HealthMonitorServer`; under the engine design the
  engine binds the `health` pipe (P4) and it becomes the **client** of
  the engine's pipe. Same wire shape (cheap connect-and-read, no
  `Engine.Hello` required), opposite end of the conversation. The
  Phase 8 worker→engine log sender (`AddEngineLoggerProvider` + the
  `EngineLoggerProvider` / `EngineLogIngestRing` / `EngineWriteLogClient`
  quartet, under `Logging/`) also lives here. This project is the
  worker-facing tip of the substrate.

**Reference graph** (acyclic). The one-way rule: `Framework.Pipes` is a
leaf that **never** depends on `Engine.*`.
`Engine.Protocol` is itself a leaf (inert cross-side DTOs). Everything
that talks to the engine — `Workers.Core`, `Engine.Core`, `Client.Core`,
and each `Worker.*` — sits above and may depend on `Engine.Protocol`.

```
Framework.Pipes (leaf)              Engine.Protocol (leaf)
        ▲                                   ▲
        └─────────────────┬─────────────────┘
                          │
         ┌────────────────┼────────────────┐
         │                │                │
   Workers.Core       Engine.Core      Client.Core
   (refs Pipes+       (refs Pipes+     (refs Pipes+
    Engine.Protocol)   Engine.Protocol) Engine.Protocol)
         ▲
         │ (optional — .NET workers wanting the host scaffold;
         │  a worker may reference the two leaves directly instead)
      Worker.*
```

`Engine.Core` and `Client.Core` reference `Framework.Pipes` +
`Engine.Protocol` directly and do **not** reference `Workers.Core`.
A .NET `Worker.*` **may** reference `Workers.Core`, which transitively
brings the rest, or may skip it and implement the dispatch protocol
directly against `Framework.Pipes` + `Engine.Protocol`; a worker on
another runtime references none of it. Engine and
dialer libraries neither bind a worker-side pipe nor dial the engine's
health probe; the dispatcher, the `CorrelationScope` helper, and the
worker→engine log sender live in `Workers.Core`.

Net effect: today's `AutoContext.Framework` keeps every line of code
it has, redistributed across sibling projects whose reference-graph
shape matches the actual consumer asymmetry and preserves a one-way,
`Framework.* → Engine.*`-free arrow. Nothing in the substrate is dead;
a few wire envelopes get extended and one client flips direction. No
new "portability interfaces" appear here — this is composition of
concrete .NET types across assemblies, exactly as
`## Sharing principle` requires.

> **Consolidation note (project graph).** As part of this rollout
> two adjacent projects are folded into the substrate:
>
> - `AutoContext.Mcp.Abstractions` (one file: `IMcpTask.cs`) moves
>   to `AutoContext.Workers.Core/IMcpTask.cs`. The project is
>   deleted.
> - `AutoContext.Worker.Shared` folds into `AutoContext.Workers.Core/`
>   — both the worker-host extensions and the worker→engine log sender
>   (`AddEngineLoggerProvider` + bounded ring + write-log client, under
>   `Logging/`). The project is deleted. (The log sender does **not**
>   land in `Framework.Logging` — that would force a
>   `Framework.* → Engine.Protocol` dependency the one-way rule
>   forbids.)
>
> Both held substrate-grade code already shaped like substrate
> content; keeping them as separate one- and five-file projects
> bought no isolation (every `Worker.*` already referenced
> `Framework`) at a real project-graph cost. After consolidation
> each `Worker.*` project drops its `Mcp.Abstractions` and
> `Worker.Shared` references and picks up a single
> `<ProjectReference>` to `AutoContext.Workers.Core` (which
> transitively brings `Framework.Pipes` and `Engine.Protocol`). The
> mechanical move happens in Phase 0 of the
> implementation plan.

## Engine binary

`autocontext-engine` is a separate .NET binary, distributed inside
each AutoContext host bundle (the VS Code extension's VSIX, the
Anthropic plugin root). Other host bundles — debug or scripting
clients, future shells — may carry their own engine copy and
spawn it themselves; those bundles are documented in their own
design docs.

One binary, **two roles**, selected by the presence of `--mcp-server`
on the command line. The roles are independent processes with no
runtime coupling — they never RPC each other, never share an
address space, and never spawn each other; the only channel between
them is the workspace's own `.autocontext.json` on disk, which both
roles read as ordinary file I/O.

- **Daemon role** (no `--mcp-server` flag) — the full engine
  described in the rest of this document: binds the four
  workspace pipes (`rpc`, `events`, `health`, `logs`), owns
  `.autocontext.json` writes, runs workspace detection, dispatches
  workers, writes an entry to `engine-registry.json`, persists
  `engine.log` + per-worker logs, runs housekeeping. This is
  what every functional client (VS Code extension, agent hooks,
  any other pipe-RPC consumer) talks to. Typical launch from a
  long-lived host:
  `autocontext-engine --workspace <path> --instance-id <uuid>
  --idle-timeout 0 --parent-pid <host-pid>`.
- **MCP-server-only role** (`--mcp-server with-stdio`) — a
  **reduced** stdio MCP server: the daemon's read capabilities
  plus on-demand worker dispatch, and nothing else. None of the
  four daemon pipes are bound, no `engine-registry.json` entry is
  written, no `engine.log` file is produced, no housekeeping runs,
  no `FileSystemWatcher` is attached, and no keep-alive /
  idle-timeout clock exists. The process speaks MCP JSON-RPC on
  stdin/stdout, logs operational events to stderr only, reads
  bundled side-car corpus (`Instructions/`, `Resources/`) from
  `AppContext.BaseDirectory`, and **re-reads `.autocontext.json`
  on every MCP request** (one stat-then-read per `tools/list` /
  `tools/call`; small JSON on warm cache, the authoritative source
  of truth at the moment the request is served). In-process
  capabilities (the `instructions_*` tools) are served directly
  off the bundled corpus with no worker involvement.
  **Worker-backed tools** (the `analyze_*` / `read_*` family) are
  served by spawning the owning worker on demand — the same lazy
  `WorkerProcessService.ensureRunning(workerId)` gate the daemon
  uses — and round-tripping over a **private** worker-dispatch
  pipe. These worker pipes are the only pipes this role ever
  binds; they are namespaced by an **ephemeral instance id minted
  internally at process start** (never accepted from argv), are
  not advertised in any registry, and are torn down when the
  MCP-server process exits. A worker spawned for one request stays
  warm for the life of the process so repeat `tools/call`s reuse
  it, matching the short-lived, host-managed nature of an MCP
  server rather than maintaining a persistent pool. The process
  exits cleanly on stdio EOF, killing any workers it spawned; the
  MCP host (VS Code's MCP manager, Claude Desktop, Claude Code)
  owns its lifecycle entirely — relaunch on crash is the host's
  job, not the engine's. Argv accepted in this role:
  `--workspace`, `--mcp-server`, `--log-level`, `--version`. Every other engine
  switch (`--instance-id`, `--instance-label`, `--idle-timeout`,
  `--parent-pid`, `--retention`, `--log-rotation`) is **rejected at
  argv parse time** — they describe daemon pipe-and-registry
  concerns this role does not have (the ephemeral worker-pipe
  scope is internal, not the launcher-minted `--instance-id`).
  `--log-level` is accepted because operational logs go to stderr
  only and the role would otherwise be undiagnosable: it defaults
  to `warning` and raising it never touches stdout, which stays
  protocol-only. A fault that escapes the host writes a
  `crash.log` tombstone under the ephemeral instance's cache
  subtree — the role's only on-disk artefact, and only on a crash.

The two roles can coexist on the same workspace without
coordination: a VS Code window runs the daemon role (state
authority over pipes), an MCP host concurrently runs the
MCP-server-only role (read-mostly view via stdio, with on-demand
worker dispatch for compute tools). Writes to
`.autocontext.json` from the daemon propagate to the MCP-server
role on the next MCP request (β-style on-demand reads); writes
from the MCP-server role propagate to the daemon through the
daemon's existing `FileSystemWatcher` → debounced reload pipeline
(see *Reload coalescing*). The cross-instance `FileShare.None`
retry rules in *Process scoping* apply uniformly — the
MCP-server role is just another concurrent reader/writer of the
same file.

### Process scoping: one engine per launcher instance per workspace

The engine is **always (workspace, launcher-instance)-scoped**.
`autocontext-engine`'s `--workspace <path>` and `--instance-id
<uuid>` arguments are both mandatory; there is no "daemon-wide"
mode that serves multiple workspaces, and there is no implicit
shared engine across unrelated launchers on the same workspace.
The reasons are structural, not incidental:

- **State is workspace-shaped.** `.autocontext.json`, the override
  directories (each `engine.instructions.overridesRoots` root's
  `instructions/` subfolder, default `<workspace>/.github/instructions/`),
  workspace-context detection results, and the per-file and per-tool
  `disabled` / `disabledRules` state are all
  per-workspace. A single process
  serving N workspaces would just be N independent state machines
  glued into one address space — no shared cache, no shared
  lifecycle, only shared crash blast radius.
- **Lifecycle is launcher-shaped.** A *launcher instance* is one
  spawn-decision point — a single VS Code window (extension + the
  hooks VS Code Copilot runs inside it share that window's
  instance), one Claude Code session, one one-shot spawner
  invocation. The launcher mints a UUIDv4 once at startup, passes
  it on `--instance-id` when it spawns the engine, and uses the
  same UUID to dial the engine's pipes thereafter. Engines
  idle-timeout when their own launcher's keep-alive clients
  disconnect; an unrelated launcher on the same workspace runs an
  independent engine with an independent idle clock.
- **Endpoint naming makes this concrete.** Every endpoint carries both
  identifiers — `autocontext-engine:<kind>@<workspaceHash>#<instanceId>`
  — so the hash identifies the workspace, the UUID identifies the
  launcher instance, and together they identify the engine. See
  [Lifecycle](#lifecycle) > *Endpoint* for the canonical format,
  the four `<kind>` values, and the normalisation rules.

Consequences:

- **`Workspace.Detect`** runs on the engine's own configured
  workspace path — the path passed via `--workspace`. It is not a
  general-purpose "detect any path" RPC. A client that wants the
  detection result for an arbitrary path on the file system spawns
  its own engine for that path with its own instance UUID and asks
  that engine for its detection result. Asking one engine to detect
  a different workspace is not on the wire.
- **A user with three workspaces open in two VS Code windows plus a
  parallel Claude Code session ends up with multiple engine
  processes** — one per (workspace, launcher) pair, each idle-timing
  out independently. This is a feature, not a bug — isolation
  matches both the existing per-workspace MCP server model and the
  natural launcher lifecycle.
- **`Agent.*` cross-broadcast is preserved within a launcher
  instance.** Hooks spawned by a launcher and that launcher's
  primary client (the extension, the Claude session) share the
  same engine, so `Agent.SubagentStarted` from a hook reaches the
  extension's tree view through `Agent.Events.Subscribe` on the
  same engine. Across launchers, agent events stay local — an
  active sub-agent in one VS Code window is invisible to a second
  VS Code window on the same workspace, by design.
- **Cross-instance `.autocontext.json` is shared on disk, not
  over the wire.** Two engines on the same workspace coordinate
  through the file system: writes use `FileShare.None` plus
  exponential-backoff retry; reads pick up peer changes through
  the engine's existing `FileSystemWatcher` and surface as the
  usual `Config.Subscribe` event. Engines never RPC each other —
  the only cross-instance channel is the workspace file system,
  which is also the channel through which any external editor of
  `.autocontext.json` is already observed. Three properties fall
  out of this and the implementation must preserve all three:

  - **Last-writer-wins on disk, mediated by the OS.** When two
    engines write concurrently, `FileShare.None` plus retry
    serialises the I/O windows — neither write can tear and
    neither observes a partially-written file — but it does not
    serialise the surrounding read-modify-write. The on-disk
    state after both writers complete is whichever payload was
    flushed second; that file *is* the canonical state, by
    construction (no leader, no quorum, no peer reconciliation).
    The same rule applies symmetrically when the second writer
    is a human in the JSON editor, `git pull`, or any other
    external mutator — the engine treats every change uniformly.
  - **Propagation to peer engines is the existing
    `FileSystemWatcher` → snapshot-swap → `Config.Subscribe`
    pipeline.** Every live engine on the workspace already
    watches `.autocontext.json` for external edits (the JSON
    editor, `git pull`, the user's text editor). A peer
    engine's write is indistinguishable from those external
    edits at the watcher boundary — same inode-change event,
    same reload path, same atomic snapshot-pointer swap (P9),
    same `Config.Subscribe` fan-out, same `Instructions.*`
    `disabled`-flag re-evaluation, same revision-counter
    bump on `Engine.Lifecycle.reloaded`. No code path is
    conditioned on whether the change originated locally or
    remotely; the watcher is the universal ingress for
    out-of-process mutations. The watcher path itself is
    coalesced — see
    [Reload coalescing: debounce and batch](#reload-coalescing-debounce-and-batch)
    for the debounce + writer-side batching rules that absorb
    FS-event bursts and in-process toggle bursts into one
    reload + one fan-out. Two consequences are worth
    naming:
    - The propagation channel is **not coupled to
      `engine-registry.json`**. The shared liveness registry
      is observability (who is alive, since when, at what
      version — consumed by external observability tools and
      tree-view
      badges); it is not a membership list any propagation
      path reads from. Tying reload fan-out to the registry
      would couple two deliberately-decoupled concerns —
      state authority (the file) versus liveness (the
      registry) — and would invite races where a registry
      lag silently drops a peer from "the set being
      notified". The file-watcher path has neither failure
      mode because each engine independently observes the
      file and never asks "who else is listening".
    - Propagation is **eventually consistent**, not
      synchronous. Engine B's `Config.Subscribe` fan-out
      does not fire in the same atomic step as engine A's
      write returning; there is a small window (FS-watcher
      debounce + reload + per-subscriber send-buffer
      enqueue, bounded by P9's non-blocking fan-out) during
      which A's clients have seen the new snapshot and B's
      have not. For interactive UI surfaces (extension tree
      view, plugin status surfaces, `instructions watch`
      terminals) the window is invisible — well under the
      threshold the eye registers. For tight automated
      tests issuing `Config.Get` on engine B immediately
      after engine A's write completes, the read can
      return the pre-write snapshot until the watcher
      drains; no current RPC promises cross-engine
      read-after-write, and any future surface that needs
      that guarantee would have to layer an explicit
      barrier on top.
  - **The lost-update window is narrow and the canonical
    upgrade is in-file optimistic CAS, not peer
    coordination.** The one collision class `FileShare.None`
    plus retry does *not* close is the read-modify-write race:
    if engine A and engine B both snapshot `.autocontext.json`
    before either writes, both compute a mutation against the
    pre-race snapshot, and both then funnel through the OS
    one-at-a-time, the second write overwrites the first
    writer's mutation and the first user's toggle is silently
    lost. The watcher heals divergence (both engines converge
    on the final on-disk state within FS-watcher latency) but
    cannot recover the dropped mutation — both writers
    committed against the same prior snapshot, so neither has
    the information needed to merge. Concretely, the window
    is `read → mutate → write` on a small JSON file under
    sub-millisecond `SemaphoreSlim` hold (P9); the realistic
    workload (a human clicking a tree-view toggle in one
    launcher while another human clicks a different toggle in
    another launcher within the same few milliseconds) is
    rare enough that the design treats it as acceptable today.
    If the rate ever justifies closing it, the canonical
    upgrade is **optimistic concurrency control inside the
    file itself** — embed a monotonic `version` integer (or
    content-hash etag) in `.autocontext.json`, have every
    writer read the version, build the new payload with
    `version + 1`, and inside the `FileShare.None` window
    re-read just the version field and abort + retry on
    mismatch. That closes the lost-update class without
    introducing any peer-engine RPC, without coupling
    propagation to `engine-registry.json`, and without
    promoting any one engine to a leader role. Peer
    coordination is **not** the canonical upgrade — keeping
    the file as the single arbiter is.
- **Workspace identity is still the path.** Path normalisation
  (uppercase on Windows, trim trailing separators; **no** symlink
  resolution — see § P4) collapses the unintentional multi-engine
  cases that would otherwise arise from path-shape differences
  alone. The launcher dimension is additive: same workspace from
  two launchers = two engines on purpose; same workspace at two
  surface-equivalent absolute paths = two engines by accident,
  which the normalisation prevents. Two surface-distinct paths
  that happen to reference the same underlying directory via a
  symlink / junction / drive substitution resolve to two engines;
  see § P4 for why we accept that trade-off.
- **Instance-id propagation is the launcher's responsibility.**
  Clients that *spawn* the engine mint the UUID and use it
  directly. Clients that need to dial an *already-running* engine
  without being the launcher (a hook script run by an external
  host process, an ad-hoc log tailer from a terminal) need to
  learn the instance-id through a side channel
  the launcher provides — typically an environment variable
  inherited from the launcher process, or a discovery file the
  launcher writes under the OS user-cache root. The exact
  side-channel is out of scope for the engine binary itself; the
  engine only consumes `--instance-id` and bakes it into its pipe
  names. The launcher contracts that propagate it are specified
  per-host (extension, CLI, plugin) in those hosts' own surfaces.

### Lifecycle

- **Pipe topology: four pipes per (workspace, launcher instance).**
  The engine binds four named-pipe servers per (workspace, launcher
  instance) pair, separated by purpose so a slow consumer on one
  transport never back-pressures another. Each
  pipe is its own `NamedPipeServerStream` (Unix domain socket on
  POSIX) accepting many concurrent client connections via
  multi-instance.

  | Kind | Purpose | Keep-alive? | Typical clients |
  |---|---|---|---|
  | `rpc` | Request/response and server-streaming RPC (`Engine.Hello`, `Config.*`, `Instructions.*`, `Workspace.*`, `McpTools.*`, `Discovery.*`, `Agent.*` notifications, `*.Subscribe` channels other than `Engine.Lifecycle`) | **yes** | every functional client that mutates state or reads state-bearing surfaces |
  | `events` | Engine-broadcast lifecycle stream (`Engine.Lifecycle.Subscribe`, future global broadcasts) | **yes** | every client that needs cache invalidation on reload / shutdown |
  | `health` | Passive readiness / heartbeat probe (cheap connect-and-read shape; no `Hello` required) | **no** | spawners deciding "is the engine up?", status probers, future monitoring |
  | `logs` | Server-streaming log tail — unified sink for engine-emitted **and** worker-emitted records, distinguished by the `category` field on every record (see *Log categories* below) | **no** | log tailers, ad-hoc `nc` / `Get-Content` debugging |

  **Why four and not one.** Isolation and separation: a forgotten
  `logs --follow` in a terminal must not pin the engine alive, must
  not back-pressure an `rpc` call, and must not require the consumer
  to speak the rpc handshake. A passive health probe must not require
  protocol-version negotiation. The `events` stream is broadcast-shaped
  (one fan-out, every subscriber sees the same envelope) and survives
  pipe-lifecycle independently of any in-flight `rpc` call. Splitting
  by kind makes each pipe's contract narrow enough to reason about on
  its own — and is the dial-only-what-you-need shape every client
  actually wants (a hook script doing one `Instructions.Get` dials
  only `rpc`; a status tool dials only `health`).
- **Endpoint** is derived deterministically from the absolute
  workspace path plus the launcher-minted instance UUID:
  `autocontext-engine:<kind>@<workspaceHash>#<instanceId>`, with
  `<kind>` ∈ {`rpc`, `health`, `logs`, `events`}, `<workspaceHash>`
  = `sha256(normalisedWorkspacePath):0..16` rendered as **uppercase**
  hex (`[0-9A-F]{16}`), `<instanceId>` = UUIDv4. Path normalisation:
  uppercase on Windows, trim trailing separators; **no** symlink
  resolution (see § P4 for the rationale). The workspace hash is one
  (P4 — one hash, four endpoints sharing it within an instance); the
  UUID is the launcher's, passed verbatim to the engine on
  `--instance-id` and reused on every dial. The transport-specific
  path prefix (`\\.\pipe\` on Windows when the transport is a named
  pipe, `${os.tmpdir()}/` on POSIX) is applied by the transport
  layer, not baked into the endpoint address.
- **Independent dial.** Clients dial only the pipes they need. The
  VS Code extension dials `rpc` + `events`; a SessionStart hook that
  only wants `Instructions.GetAlwaysAttached` dials `rpc`; a status
  probe dials `health`; a log tailer dials `logs`. There is no
  requirement to dial all four,
  and no implicit cross-pipe correlation — each pipe is an
  independent transport. A client that wants invalidation signals
  must explicitly dial `events`; one that doesn't, won't see them.
- **Cold start (try-connect-with-retry, no pre-flight).** A client
  that is also the launcher mints its instance UUID, attempts to
  connect to the pipe it wants (using
  `autocontext-engine:<kind>@<workspaceHash>#<thisInstanceId>`); on
  failure it asks a single spawner abstraction to spawn
  `autocontext-engine --workspace <path> --instance-id <uuid>`
  detached and retries against two budgets, both independent of
  `Engine.Hello`:
  - **Warm connect (no spawn):** sub-second.
  - **Cold connect (after spawn):** up to a few seconds with
    exponential backoff. A self-contained .NET process binding
    its four pipes routinely takes hundreds of milliseconds on
    first launch, more under load. The engine binds all four
    pipes before accepting on any of them, so a successful
    connect on one pipe guarantees the other three are also
    bound — clients never have to retry sibling pipes
    independently.

  No cross-platform pipe-existence pre-flight: existence tests for
  Unix sockets are unreliable; a single try-connect is the canonical
  probe (the cheapest pipe to dial for that purpose is `health`).
- **Concurrent first-connect within a launcher instance.** When
  two clients of the *same* launcher race (e.g. the extension and a
  hook spawned by the same VS Code window, both holding the same
  instance UUID), the spawner serialises and ensures at most one
  engine process actually starts; the loser of the race re-enters
  the connect-retry loop against the winner. **`<instanceId>` is
  per-launch and never reused** (P4): a second engine process
  actually starting under the same `<instanceId>` is a launcher
  bug — the engine fails loudly on pipe-bind collision with a
  non-zero exit and a diagnostic log line naming the colliding
  pipe. The engine does **not** treat the collision as a normal
  shape that bind has to be idempotent against; it is an invariant
  violation by the launcher contract. Two launchers on the same
  workspace dial different endpoints (different `<instanceId>`
  suffix) and start independent engines by design — that is not a
  race, that is two engines.
- **Wire-protocol handshake.** `Engine.Hello` is an `rpc`-pipe RPC.
  After connecting `rpc`, the client issues `Engine.Hello` *before*
  any other RPC, capped by an independent short budget. `events` requires
  the same handshake (first frame is a `Hello` envelope sharing
  the same protocol-version integer) so a misversioned client cannot
  silently subscribe to a stream it would misparse. `health` and
  `logs` do **not** require `Hello` — both are passive read-shaped
  surfaces whose payload shape is **additively versioned**: `health`
  emits one small status JSON document, `logs` emits one structured
  log record per line (NDJSON; the record schema is documented under
  the `Engine.WriteLog` RPC and the *Log categories* subsection).
  New fields may appear on either payload over time, but consumers
  parsing well-formed JSON and ignoring unknown fields stay
  forward-compatible without protocol-version negotiation — which is
  the actual stability property the no-`Hello` rule needs, and the
  reason these two pipes can skip the handshake the framed `rpc` and
  `events` pipes require.
  The protocol version is an integer constant bumped on every
  wire-format change. **Compat rule: exact-match required.** Engine
  and client must agree on the integer; mismatch in either direction
  refuses. Each host ships its own bundled `autocontext-engine`
  (inside the VSIX, inside the Claude plugin root), and the release
  process versions hosts together — a handshake mismatch in
  production is a packaging bug, not a scenario the protocol tries
  to recover from. Clients surface the refusal as a hard error
  (CLI exit 69, hook structured failure); we do not try to negotiate
  down.
- **Warm reuse within a launcher instance.** Subsequent clients of
  the *same* launcher (the extension and the hooks running in the
  same VS Code window, a long-lived Claude session and a one-shot
  diagnostic dialled by a hook script with the same instance UUID)
  connect to the existing engine's pipes. State is consistent
  across all of them — the four pipes share one engine process,
  one in-memory state store, one revision counter. A *different*
  launcher (a second VS Code window on the same workspace, a
  parallel Claude Code session) starts its own engine and gets its
  own in-memory state; the only state they share is what lives on
  disk in the workspace.
- **Idle shutdown.** The engine exits after `--idle-timeout` seconds
  with no **keep-alive clients** connected (default 300), with a
  fixed **2-second grace period** after the last keep-alive
  disconnect to absorb VS Code reload churn (extension-host
  restart, language-service refresh). Only `rpc` and `events`
  connections count toward the keep-alive gate; `health` and `logs`
  are passive observers and do **not** pin the engine alive — a
  forgotten `logs --follow` terminal or a polling monitor on
  `health` cannot prevent idle shutdown. When the gate fires while
  `health` or `logs` clients are still connected, the engine emits
  `shutting-down` on `events` (for any subscribers there), closes
  all four pipes, and exits; passive observers see a clean EOF.
  **`--idle-timeout 0` disables this gate entirely** — the engine
  lives until an external lifecycle clamp fires (the
  `Engine.Shutdown` RPC, a SIGINT / SIGTERM, the optional
  `--parent-pid` watchdog described below). This is the right mode
  for long-lived host launchers (the VS Code extension, a Claude
  Code session, an externally-shutdown-able engine)
  where the host already owns lifecycle and the idle clock would
  just be a second exit-path racing the first. Short-lived
  spawners (one-shot hooks, the CLI's find-or-spawn flow) keep the
  default so a forgotten engine still cleans up.
- **Crash recovery.** Stale pipe handles surface through the same
  try-connect-with-retry path: a failed connect is treated as "engine
  absent" and triggers a respawn. Because the four pipes are bound
  together by one process, a stale-on-one is stale-on-all — the
  respawn replaces the whole quartet atomically.
- **MCP-server-only role is out of scope for this section.** The
  `Lifecycle` rules above (four pipes, `Engine.Hello` handshake,
  keep-alive accounting, idle-timeout, crash recovery, registry
  sweep) describe the **daemon role** exclusively. When the engine
  is launched with `--mcp-server with-stdio` it runs the
  MCP-server-only role instead, and **none of those mechanisms
  apply**: the process binds none of the four daemon pipes (its
  only pipes are the private, on-demand worker-dispatch pipes
  described in *Engine binary*), performs no `Hello`
  handshake (the wire protocol is MCP JSON-RPC on stdio, not the
  engine's pipe RPC), does not write an `engine-registry.json`
  entry, does not participate in the keep-alive gate or the
  idle-timeout clock, does not run the housekeeping sweep, and
  does not attach a `FileSystemWatcher`. Lifecycle is whatever
  the MCP host gives it: the process exits on stdio EOF; if it
  crashes, the MCP host relaunches it. The only state coupling
  to a concurrent daemon (when one exists on the same workspace)
  is `.autocontext.json` on disk — the MCP-server role
  stat-then-reads the file on **every** incoming MCP request
  (`tools/list`, `tools/call`, `prompts/*`, etc.), so a daemon's
  write is observed by the next request without any subscription
  or invalidation channel. Conversely, an MCP-server-role write
  reaches a peer daemon through that daemon's existing
  debounced-watcher path (see *Reload coalescing*), bounded by
  FS-watcher latency — the same eventual-consistency window the
  *Process scoping* section documents for daemon-to-daemon
  propagation. There is no in-process state to share because
  there is no concurrent in-process state — the two roles are
  separate processes by construction.

  Non-MCP daemon spawners (extension, agent plugin, any other
  pipe-RPC client that cold-spawns its own engine) launch the
  engine with `stdio: 'ignore'` precisely so the
  SDK's read loop can't hit immediate EOF on a `/dev/null` stdin
  and self-terminate the process — that footgun only exists when
  `--mcp-server` is set; the daemon role does not register any
  MCP transport and leaves its own stdin/stdout untouched.

### Revision counter

The engine tags every published state snapshot with a
**revision counter** — a monotonically increasing integer that
serves as the cache-invalidation key clients use to answer "is
my view current?" without diffing payloads.

- **Type and range.** `long` (64-bit signed) on the wire and in
  the engine's in-memory state. Picked over `int` to remove
  overflow from the failure surface entirely: at the writer
  micro-batch ceiling of ~100 bumps/second (see *Reload
  coalescing* below) a `long` outlasts the universe; a 32-bit
  counter would overflow in ~243 days of nonstop hostile
  toggling. The 4-byte saving is not worth the wrap-comparison
  complexity it would otherwise force on every comparison site.
- **Per-instance, resets on every spawn.** The counter is held
  in memory only; it starts at 0 when the engine process
  starts and dies with the process. There is no persistence
  across restarts — a client that cached "revision 42"
  yesterday will see "revision 3" today and **must not** treat
  3 as older than 42.
- **Cross-restart dedup uses `(instanceId, revision)` as a
  compound key.** The `<instanceId>` segment (P4 — fresh UUID
  per spawn) names *which* engine emitted the revision; the
  revision orders snapshots *within* that engine. Clients
  compare revisions only when the `instanceId` matches; a
  different `instanceId` means "different engine, throw the
  cache out wholesale." `Engine.Lifecycle.Subscribe` emits a
  `started` event carrying the current `(instanceId, revision)`
  pair on every fresh subscribe, which is the signal clients
  use to detect "the engine you remember is gone; here's the
  new identity."
- **What it counts.** *Snapshot swaps*, not change events.
  The counter increments once per atomic snapshot-pointer
  swap — once per coalesced writer batch, once per reload
  that produced a non-equal snapshot, never on a deep-equal
  no-op reload (see *Reload coalescing* below).
- **Where it rides on the wire.** State-bearing reads
  (`Config.Get`, `Workspace.Info`), state-change broadcasts
  (`Config.Subscribe`, `Instructions.Subscribe`,
  `Engine.Lifecycle.reloaded`), and the writes that
  produce new snapshots (`Config.ToggleFile` /
  `Config.ToggleRule` replies) all carry it. Surfaces that
  don't reflect snapshot state — logs (`LogRecord`,
  `Engine.WriteLog`, `Logs.*`), the `Engine.Hello` handshake,
  `McpTools.Invoke`, `Workspace.Detect`, `Discovery.*`,
  `Agent.*` fire-and-forget notifications, lifecycle acks
  (`Engine.Shutdown` reply) — do not carry it.

### Reload coalescing: debounce and batch

The engine's reload pipeline (re-read `.autocontext.json`, rebuild
the immutable snapshot, atomic pointer swap, fan out
`Config.Subscribe` plus dependent `Instructions.*` deltas, bump
the revision counter on `Engine.Lifecycle.reloaded`) is **the
single ingress** for every state change. Both in-process writes
(`Config.ToggleFile`, `Config.ToggleRule`) and out-of-process
mutations (peer engines, the JSON editor, `git pull`, scripts)
end up here. Two coalescing rules apply to that ingress; neither
is optional and they solve different problems.

- **Debounce — coalesce physical FS events into one logical
  reload.** `FileSystemWatcher` (and `inotify` / `FSEvents` / the
  polling watcher under WSL+SMB) emits a *burst* of events for
  one user-perceived save: atomic-rename saves produce
  `Created` → `Deleted` → `Renamed` (plus a stray `Changed`);
  in-place truncating saves produce two or three back-to-back
  `Changed` events; cross-platform inconsistency means the same
  logical edit can land as 1 event on macOS, 3 on Windows, and
  5 under WSL forwarding. Without coalescing the engine reloads
  N times for one toggle, fans out N redundant snapshots, and
  bumps the revision counter N times — clients re-render
  flickeringly and smoke tests turn racy. Shape:
  - **Trailing-edge debounce per watched resource.** The
    watcher callback resets a per-resource timer and does
    nothing else; the read happens on timer fire. ~75–150 ms
    is the target window — invisible interactively, long
    enough to absorb every observed burst shape across the
    supported file systems. Trailing-edge (not leading-edge)
    is mandatory: an atomic-rename burst starts with a
    `Created` on a temp file that does not yet exist under
    the canonical name; reacting to it would read nothing.
  - **The debounce is the read barrier.** The engine never
    reads inside the watcher callback. Reading on timer fire
    means the file is no longer mid-rename and no longer
    mid-flush, so the cross-engine `FileShare.None` backoff
    retry only has to defend against a concurrent *peer
    write* — not against the engine's own watcher firing
    inside someone else's rename window.
  - **One timer per resource.** `.autocontext.json` is the
    only watched resource today; the same shape applies the
    day the engine watches `instructions/<name>.instructions.md`
    for hot-reload — a different timer per file, so a config
    save never resets the timer for an unrelated instructions
    edit (and vice versa).
  - **Cancellation propagates.** Engine shutdown (SIGINT,
    SIGTERM, idle-gate fire) cancels every pending debounce
    timer through the engine's root `CancellationToken`
    (P8); no fire-and-forget timer outlives the process.
  - **Deep-equal short-circuit (also the self-write
    suppressor).** If the post-debounce read parses to a
    config structurally equal to the current in-memory
    snapshot (a formatter ran, `git checkout` restored the
    same content, a peer rewrote the file with the same
    payload, **or the engine itself just wrote this file**),
    the reload pipeline skips the snapshot swap and the
    fan-out — nothing to publish. The fast path is a content
    hash of the source bytes against the hash the current
    snapshot was built from; the slow path is a deep-equal
    walk of the parsed config. The revision counter is
    **not** bumped on a no-op reload; bumping it would
    falsely invalidate every client's cache for a benign
    disk touch. Crucially, this rule **is** the self-write
    suppression mechanism: every local `Config.Toggle*` ends
    by publishing the new in-memory snapshot synchronously,
    so when the watcher fires shortly after on the same
    write, the on-disk parse is by construction equal to
    that just-published snapshot — the short-circuit fires
    and the watcher echo is silently absorbed. Without this,
    every local toggle would cost *two* fan-outs (one from
    the writer, one from the watcher seeing its own write);
    with it, the writer's fan-out is the only one clients
    see.
  - **Tunable, not user-facing.** The debounce window is an
    `EngineOptions` constant exposed for tests, not a CLI
    flag. The right value is empirical; users have no use
    for tuning it.

- **Batch — coalesce in-process toggles into one write.** A user
  clicking "disable all instructions in this folder" in the tree
  view, a script firing three back-to-back `Config.Toggle*` RPCs,
  an MCP host hook firing multiple routing-driven toggles in one
  user turn — each is a *single logical bulk action* arriving
  as N `Config.Toggle*` RPCs in tens of milliseconds. Without
  batching the writer mutex is taken N times, the file is
  rewritten N times (each rewrite costs an FS-share retry
  window for peers), the watcher fires N times, and the
  fan-out happens N times. The work is not wasted — every
  toggle does correspond to a real state change — but the
  surface is chatty and amplifies cross-engine traffic
  unnecessarily. Shape:
  - **Coalesce on the writer side, under the same async
    mutex P9 already mandates.** When a write completes and
    the writer is about to release the mutex, it peeks the
    queue of pending toggles for further entries arriving
    within a short window (~5–10 ms — much shorter than the
    FS debounce; this is the in-process path) and folds
    them into the same snapshot before flushing to disk.
    One on-disk write, one snapshot swap, one fan-out frame.
  - **`Config.Subscribe` carries the batch as one envelope.**
    The wire shape is `{ revision, changes: [...] }`
    where `changes[]` lists every mutation in writer-mutex
    order; the revision counter increments once per batch,
    not once per change. Clients that need to react
    per-change iterate `changes[]`; clients that just need
    "something changed" check the revision counter. Order
    within `changes[]` is writer-mutex order, *not* a
    semantic temporal claim — clients must not infer
    causality from position.
  - **Self-write suppression is the deep-equal short-circuit
    above, not a separate mechanism.** Every local
    `Config.Toggle*` publishes the new in-memory snapshot
    synchronously at the end of the write, *then* releases
    the writer mutex; the watcher's echo on the engine's
    own write arrives shortly after and the debounced
    reload finds the on-disk parse structurally equal to
    the just-published snapshot, so the short-circuit fires
    and the echo is absorbed. The writer does **not** need
    to stamp the file with a marker, set an
    "expecting-this-event" flag, or otherwise track its
    own writes — the equality check is sufficient and is
    also what catches benign external touches (formatter,
    `git checkout` of the same content). The day the
    optional in-file `version` field lands (see the
    cross-instance lost-update note), it becomes the cheap
    fast-path inside the equality check, but the rule does
    not depend on it.
  - **Slow-subscriber semantics are unchanged.** P9's
    per-subscriber bounded buffer applies to the batch
    envelope as one frame; a subscriber that misses it
    through eviction catches up on resubscribe via the
    snapshot-on-subscribe contract (P6). A missed batch
    does not silently lose N changes — the resubscribe
    snapshot contains every applied mutation.

The two mechanisms compose cleanly across the cross-instance
case. A peer engine's batched write flushes to disk **once**
(end of batch); each peer's `FileSystemWatcher` sees the burst,
the debounce coalesces it into one reload, and the peer's
clients receive one batch envelope describing every change.
External edits (text editor, `git pull`, scripts) take the same
path on every peer's debounce — there is one rule, applied
uniformly:

| Origin | Local fan-out | Peer fan-out |
|---|---|---|
| Local API burst (N `Config.Toggle*` RPCs) | 1 (writer-side batch) | 1 (peer's debounce coalesces one write burst) |
| External edit (JSON editor, `git pull`, script) | n/a (no local API call) | 1 (debounce coalesces the editor's save burst) |
| Peer engine's batched write | 1 (debounce coalesces the peer's single flush) | n/a (each peer sees the other) |

Failure modes the rules prevent: re-render flicker on every
external save (`N` watcher events → `N` re-renders), runaway
self-fan-out on local toggles (writer → watcher → writer-shaped
reload → watcher again), spurious cache-invalidation on benign
disk touches (a formatter rewrite incrementing the revision
counter even though nothing changed semantically), and
cross-engine amplification (a bulk action on engine A producing
N FS events on every peer instead of one).

### Batching policy

The engine has **one batching direction: server → client**. Clients
send one logical operation per RPC frame; the engine may coalesce
multiple state changes into one server-streamed envelope when they
come from the same snapshot swap. Defining the rule explicitly
stops two failure modes: clients reinventing client-side batch RPC
on top of the wire protocol, and event streams growing ad-hoc
multi-event envelopes that subscribers can't reason about uniformly.

- **No client-side batch RPC.** The engine does not expose a
  JSON-RPC batch-array surface or a generic "multi-call" RPC.
  One RPC frame = one method invocation = one response envelope
  (P1, P2). The cost batch RPC traditionally amortises
  — round-trip latency over a network transport — doesn't exist
  on a same-machine named pipe; adding the wrapper would only
  complicate the error model (all-or-nothing vs partial success,
  per-element cancellation, what happens if one element is a
  `*.Subscribe`) without buying performance.
- **The natural "batch this" pressure is on state-mutating
  writes**, and it is already handled server-side by the writer
  micro-batch (see *Reload coalescing*). N back-to-back
  `Config.Toggle*` calls produce **one** on-disk write, **one**
  snapshot swap, and **one** fan-out envelope describing every
  mutation — the client does not need to opt into batching, and
  cannot opt out of it.
- **Server-streamed batch envelopes are allowed only when the
  events share one coalesced snapshot.** An envelope on
  `*.Subscribe` MAY carry multiple discrete change entries if and
  only if they were produced by one writer micro-batch or one
  debounced reload — the same `revision` bump covers them all.
  Streams whose events come from sources with no batching pressure
  stay strictly one-event-per-envelope.

Per-stream contract:

| Stream | Source of events | Envelope shape | Batch? |
|---|---|---|---|
| `Config.Subscribe` | snapshot swaps | `{ revision, changes: [...] }` | yes |
| `Instructions.Subscribe` | snapshot swaps (piggybacks on the same reload pipeline as `Config.Subscribe`) | `{ revision, changes: [...] }` | yes — same `revision`, same writer-mutex order |
| `Engine.Lifecycle.Subscribe` | process transitions (`started`, `reloading`, `reloaded`, `shutting-down`) | one event per envelope | no |
| `Agent.Events.Subscribe` | engine re-broadcast of the `Agent.*` notifications (`SubagentStarted`, `SubagentStopped`, `Compacted`, `ToolUsed`, `TurnEnded`) | one event per envelope | no |
| `Logs.TailEngine` / `Logs.TailWorker` | log records | one record per frame | no |

The `changes[]` array on the `Config.Subscribe` /
`Instructions.Subscribe` shared envelope lists every mutation in
writer-mutex order, **not** in semantic temporal order — clients
must not infer causality from position (see *Reload coalescing*).
For the non-batch streams, every event carries one discrete
payload because each event is individually meaningful for UI
rendering (lifecycle transitions, sub-agent activity) or audit
(log records), and coalescing would defeat the consumer's purpose.

### Housekeeping

The engine self-manages every on-disk artefact it produces, on a
**single clock**: a **shutdown sweep** runs as part of every engine's
graceful exit, after the engine removes its own registry entry. No
startup sweep, no external sweeper, no periodic while-alive timer —
every graceful shutdown of any engine on the machine pays the
housekeeping cost on behalf of every dead peer, which scales
automatically with how often engines actually shut down cleanly.

- **Shutdown sweep (mandatory, best-effort).** On
  `AppDomain.ProcessExit` / SIGTERM / Windows service-stop, the
  engine first removes its own entry from
  `…\autocontext\engine-registry.json`, then enumerates every
  remaining entry in the registry plus every sibling
  `…\autocontext\<workspaceHash>\<instanceId>\` directory under
  the autocontext root, and classifies each. The sweep is bounded
  by a short deadline (≤ 1 s) so a slow filesystem can't hang
  shutdown; whatever the sweep doesn't reach this time, the next
  graceful shutdown of any peer catches. Crash paths skip the
  sweep entirely — the entry and subtree stay until any subsequent
  peer's graceful shutdown reaps them.
  - **Registered entry, live** (`pid` exists AND `Process.StartTime`
    ≈ `processStartTimeUtc` within ~1 s tolerance): skip, regardless
    of whether the matching subtree exists yet.
  - **Stale registration with subtree** (pid missing OR start-time
    mismatch): owning engine is dead. If `now - startedAt` ≥ the
    entry's `retention` duration, delete the matching per-instance
    subtree (whole tree — `logs\` + `cache\`) and drop the entry;
    otherwise leave both in place and let a later peer re-check.
  - **Stale registration without subtree**: subtree was already
    swept in a previous pass (or removed out-of-band) but the
    entry remained. Drop the entry unconditionally — there is
    nothing to retain.
  - **Unregistered subtree** (directory exists, no matching entry):
    a crash before the entry was durably appended, a pre-registry
    leftover, or a legacy flat-shape `<workspaceHash>#<instanceId>`
    directory from before the nested layout. Use the directory's
    mtime as the timestamp and honour *this engine's own*
    `--retention` (no entry = no peer's preference to respect).
- **Retention is per-entry.** Each engine writes its `--retention`
  value into its own registry entry (see `Engine.RegistryEntries`
  shape under `### RPC surface`). A peer sweeping that entry honours
  *the dead engine's* declared retention, not its own — a
  long-retention engine can crash and its leftovers stay the
  configured window even if every subsequent engine declares
  `--retention 0`. Unregistered subtrees fall back to the sweeping
  engine's own `--retention` (no per-entry preference to respect).
- **Concurrency.** Two engines shutting down near-simultaneously
  both run the shutdown sweep; both pid-check the *same* peer's
  entry, both decide to delete the *same* subtree.
  `Directory.Delete(recursive: true)` under contention is
  best-effort: one engine succeeds, the other sees
  `DirectoryNotFoundException` mid-walk and treats it as
  already-cleaned (no error). Registry-entry removal is similarly
  idempotent. Registry-entry appends at *start* time use
  `FileShare.None` plus exponential-backoff retry so two engines
  starting concurrently serialise their appends; neither corrupts
  the file. Because every spawn mints a fresh `<instanceId>`,
  two concurrent appends never collide on identity — both entries
  land additively.
- **Never-graceful-shutdown edge case.** A user who only ever
  hard-kills their engines (no SIGTERM, no `Engine.Shutdown`)
  will accumulate per-instance subtrees indefinitely. This is
  accepted for v1: a CLI command (`autocontext engine
  housekeep`, deferred) can run the sweep on demand, and the
  next graceful shutdown of any engine on that machine catches
  up unconditionally. The design trades worst-case unbounded
  growth on a pathological host pattern for a strictly simpler
  lifecycle on every other host.
- **Log rotation (within-instance, driven by `--log-rotation`).** The
  engine's own `engine.log` and per-worker `worker-<workerId>.log`
  files rotate in-process by line-count or size threshold:

  | Rotation size | Rotation threshold |
  |---|---|
  | `small` (default) | 1,000 lines OR 5 MB, whichever fires first |
  | `large` | 5,000 lines OR 25 MB, whichever fires first |

  When a threshold fires, the engine renames the active file to
  `engine-<iso8601>.log` (and worker equivalents to
  `worker-<workerId>-<iso8601>.log`) and opens a fresh active file.
  `<iso8601>` is the rotation timestamp in UTC with `:` stripped —
  e.g. `engine-20260511T143052Z.log`. Active files always live at
  the stable names (`engine.log`, `worker-<workerId>.log`);
  postmortem readers and `Logs.Get*` / `Logs.Tail*` RPCs read the
  **active file only**, never the rotated history. Rotated files
  are filesystem-inspection artefacts, kept around for `grep` and
  future history-aware tooling.
- **Rotated-file retention.** Rotated logs are subject to the same
  `--retention` window the housekeeping sweep uses. The engine
  prunes its own rotated logs as part of the rotation event
  itself — a cheap scan of its own `logs\` directory looking for
  files matching the rotation pattern whose `<iso8601>` is more
  than `--retention` ago. No separate timer is needed. Rotated
  logs of *dead* peers get swept together with their containing
  per-instance subtree by the cross-instance sweep above; the
  per-file retention check on rotated files only applies to the
  living engine's own subtree.
- **What never gets housekept by the engine.** The shared registry
  file itself (the engine only touches its own entry),
  `<workspace>/.autocontext.json`, and
  `<workspace>/.github/instructions/` are outside the per-instance
  cache root and outside housekeeping scope. Client cache subtrees
  under a *live* instance's `cache\` remain client-owned (P5); only
  when the owning instance is verifiably dead does the engine sweep
  delete the whole per-instance subtree, cache and all.

### Engine options (CLI surface)

The engine accepts exactly nine command-line switches; anything
else is rejected at argv parse time with a non-zero exit and a
one-line **stderr** error listing the accepted set (never stdout —
under `--mcp-server with-stdio` stdout is the MCP JSON-RPC channel
and any stray write corrupts it).

The table below is the **daemon role** surface (no `--mcp-server`).
In the **MCP-server-only role** (`--mcp-server with-stdio`) the
argv parser accepts a **strict subset** — `--workspace`,
`--mcp-server`, `--log-level`, `--version` — and **rejects every other switch in
the table** (`--instance-id`, `--instance-label`, `--idle-timeout`,
`--parent-pid`, `--retention`, `--log-rotation`) with a non-zero exit
and a stderr error naming the rejected switch and the active role.
The rejected switches describe pipe-and-registry concerns the
MCP-server role does not have; silently ignoring them would let a
misconfigured host believe it had configured something it had not.
See [Engine binary](#engine-binary) for the role split itself.

| Switch | Required | Value | Default | Set by |
|---|---|---|---|---|
| `--workspace <path>` | yes | absolute workspace path | — | every spawner |
| `--instance-id <uuid>` | yes | UUIDv4 | — | every spawner |
| `--instance-label <text>` | no | short freeform descriptor (≤ 200 printable-ASCII chars, no control chars or newlines) | empty | every spawner that wants observability |
| `--idle-timeout <seconds>` | no | non-negative integer (`0` = disable idle gate; host-driven shutdown only) | `300` | optional override |
| `--parent-pid <pid>` | no | positive integer; engine self-exits when that process vanishes | unset | long-lived host launchers |
| `--retention <duration>` | no | duration string (`<n>{s\|m\|h\|d}`; `0` = sweep immediately) | `1d` | optional override |
| `--log-level <level>` | no | `trace` \| `debug` \| `information` \| `warning` \| `error` \| `critical` \| `none` | host configuration | optional override |
| `--log-rotation <size>` | no | `small` \| `large` | `small` | optional override |
| `--mcp-server <mode>` | no | `with-stdio` (the only accepted value today) | off | MCP hosts only |
| `--version` | no | — | — | humans, CI |

Semantics:

- **`--workspace <path>`** is mandatory. The engine pins to one
  workspace; there is no daemon-wide mode and no auto-detection
  from the working directory (P4 — workspace identity is the path,
  not the launcher's CWD).
- **`--instance-id <uuid>`** is mandatory. The launcher mints a
  UUIDv4 once per launcher instance (one VS Code window = one UUID
  shared by the extension and the hooks VS Code Copilot runs in
  that window; one Claude Code session = one UUID; one one-shot
  spawner invocation = one UUID) and passes the
  same UUID on every spawn and every dial for the life of that
  launcher. The engine validates the value matches the UUIDv4
  shape (lowercase hex, hyphenated) and rejects malformed input;
  it does not interpret the bytes further. The UUID becomes the
  `<instanceId>` segment of every endpoint (see `### Lifecycle`
  > endpoint), which is how clients dial the right engine without
  any runtime discovery: the launcher already knows the UUID it
  minted, so it already knows the full endpoint address before the
  engine has even started. Non-launcher clients (a hook running
  under a host process the launcher did not control, an ad-hoc
  an ad-hoc terminal client) learn the UUID through
  a host-specific side channel — this propagation is the
  launcher's responsibility and out of scope for the engine
  binary.
- **`--instance-label <text>`** is an optional, freeform
  human-readable descriptor the launcher attaches to this engine
  instance purely for observability. The convention is a
  semicolon-separated list of `<component> (v<version>)`
  fragments naming the launcher and the engine build it spawned
  — e.g. `vscode (v0.9.5); engine (v0.9.5)` from the VS Code
  extension, `claude-code (v1.2.0); engine (v0.9.5)` from a
  Claude session, `autocontext (v0.9.5); engine (v0.9.5)` from the
  CLI — but the engine treats the value as an opaque string. It
  is validated only for shape (≤ 200 chars, printable ASCII
  only, no control characters, no newlines, no embedded `\r`
  / `\t` that would break structured logging); content beyond
  that is the launcher's choice. The engine captures the label
  into `EngineOptions.InstanceLabel`, attaches it as a structured
  field on every log line (so postmortem reading
  `…\autocontext\<workspaceHash>\<instanceId>\logs\engine.log`
  reveals which host launched the engine without cross-referencing
  the UUID against external state), and surfaces it on the
  `Workspace.Info` RPC and the `health` pipe payload so tree views
  and external observability tools can render it. The label has **no**
  semantic effect on engine behaviour: it does not appear in pipe
  names, does not appear in on-disk paths, and is never used for
  routing, identity comparison, or compatibility decisions. Two
  engines with the same label are not the same engine; two engines
  with different labels are not different engines (the
  `<instanceId>` is what distinguishes them). When the label is
  omitted the engine records the absence as an empty string and
  emits a one-time info log warning that observability will be
  reduced — the warning is informational, never an error.
- **`--idle-timeout <seconds>`** overrides the 300-second default.
  Daemon-role only — the MCP-server-only role has no idle gate
  (it exits on stdio EOF; the switch is rejected at argv parse
  there). In the daemon role the idle gate counts only `rpc` and
  `events` keep-alive connections (see `### Lifecycle` >
  *Idle shutdown* for the full keep-alive contract). The value
  is a **non-negative** integer; `0` is the explicit "disable the
  idle gate" sentinel — the engine then lives until killed by
  signal, by `Engine.Shutdown`, or by the optional `--parent-pid`
  watchdog, and the keep-alive accounting becomes observability
  only (still recorded on the `health` payload, no longer driving
  exit). Long-lived host launchers (the VS Code extension, a
  Claude Code session, any spawner that already owns its engine's
  lifecycle) should pass `--idle-timeout 0`; short-lived spawners
  keep the default so a forgotten engine still cleans itself up.
- **`--parent-pid <pid>`** is an optional watchdog. When set, the
  engine watches the named OS process via
  `Process.GetProcessById(pid)` plus `WaitForExitAsync` on a
  background task tied to the engine's root `CancellationToken`
  (P8) and self-exits cleanly when that process vanishes — same
  shutdown sequence as a SIGTERM (emit `shutting-down` on `events`,
  drain `rpc`, close all four pipes, run the shutdown
  housekeeping sweep). The intent is to clamp the engine's
  lifetime to the *spawner's* lifetime when `--idle-timeout 0`
  removes the quiet-based exit path: the VS Code extension
  spawning an engine with `--idle-timeout 0 --parent-pid
  <vscode-pid>` gets an engine that lives as long as the editor
  window and no longer, even if the editor crashes without
  calling `Engine.Shutdown`. Pid recycling is defeated the same
  way the registry sweep does it — the engine captures the
  parent's `Process.StartTime` on bind and treats a recycled pid
  (start-time disagreement) as "parent gone". Validation rejects
  non-positive integers and a `pid` that does not currently
  resolve to a live process; once watching has started, the
  switch is purely observational from argv's perspective (the
  watchdog runs as a hosted service inside the engine). Without
  this switch the engine has no opinion about its spawner — the
  parent-child relationship is OS-only, not protocol-level.
- **`--mcp-server <mode>`** is **the capability switch**, not a
  transport switch. Today's only accepted value is `with-stdio`,
  which registers `AddMcpServer().WithStdioServerTransport()...`
  on the engine's DI graph (the same wiring today's
  `AutoContext.Mcp.Server` does at `Program.Main`). The value
  shape leaves room for `--mcp-server with-http` if a future
  engine version needs MCP over Streamable HTTP without renaming
  the switch. Unknown values are hard-rejected; the engine never
  silently falls back to a different mode.
- **`--retention <duration>`** overrides the 1-day housekeeping
  retention window. Accepts a duration string `<n>{s|m|h|d}`
  (`30s`, `15m`, `12h`, `7d`); `0` disables retention entirely
  (sweep deletes immediately on shutdown, no grace period). The
  value is validated for shape on argv parse and rejected if
  malformed; there is no host-wide minimum or maximum. The engine
  writes this value into its own `engine-registry.json` entry so
  peer engines doing the shutdown sweep honour *this* engine's
  declared retention when classifying its leftover subtree as
  stale (see `### Housekeeping`). The same window governs
  rotated-log pruning within the engine's own per-instance
  subtree.
- **`--log-level <level>`** sets the minimum level a record must
  carry to be emitted, for the engine's own records and the worker
  records it ingests. Omitting it leaves the host's own logging
  configuration in force (`information` unless configured otherwise);
  `debug` and `trace` raise volume substantially, so pair them with
  `--log-rotation large` to keep a diagnostic session in fewer files.
- **`--log-rotation <size>`** sets the in-process rotation
  thresholds for the engine's own `engine.log` and per-worker
  `worker-<workerId>.log` files. Accepted values are `small`
  (default; rotate at 1,000 lines OR 5 MB) and `large` (rotate at
  5,000 lines OR 25 MB). The switch does **not** change which
  records are emitted — log level filtering remains an in-process
  configuration concern, separate from rotation policy — only the
  size and line thresholds at which rotation fires. Unknown values
  are hard-rejected. See `### Housekeeping` > *Log rotation* for
  the rotation file-naming convention and the `Logs.*` RPC
  active-file contract.
- **`--version`** prints the engine's informational version (from
  `AssemblyInformationalVersionAttribute`) and exits. RID-independent.

Library-only knobs (not CLI flags). The `EngineOptions` callback
on `AddAutoContextEngine(...)` exposes additional knobs that
**deliberately do not surface on the command line** — corpus root
override, endpoint override, and any future implementation-only
tuning. These are reachable only by in-process composition (tests,
embedders that call `AddAutoContextEngine` directly); the binary's
argv parser rejects them. The endpoint override in particular
breaks P4's "one hash, reused everywhere" invariant, so keeping
it off the CLI surface is intentional — production hosts have no
way to set it.

### RPC surface (initial)

- `Engine.Hello` — handshake, returns
  `{ protocolVersion: <int>, engineVersion: <semver> }`. Issued by
  every client immediately after connect; mismatch on the integer
  refuses the engine.
- **`Engine.RegistryEntries`** — returns the current contents of
  the machine-wide engine-liveness registry
  (`…\autocontext\engine-registry.json`) as an array of entries, one
  per live engine the registry knows about:

  ```
  Array<{
    workspaceHash:       string,   // sha256(normalisedWorkspacePath):0..16
    workspacePath:       string,   // absolute, normalised workspace root the hash was derived from
    instanceId:          string,   // UUIDv4 the launcher minted
    instanceLabel:       string,   // freeform descriptor from --instance-label
    processId:           number,   // OS process id of the engine
    processStartTimeUtc: string,   // ISO-8601, used with processId to defeat pid recycling
    engineVersion:       string,   // semver from AssemblyInformationalVersionAttribute
    startedAt:           string,   // ISO-8601 — when this entry was written
    retention:           string    // duration string from --retention (e.g. "1d", "12h", "0")
  }>
  ```

  The engine reads the file when answering this RPC; it does not
  maintain an in-memory mirror, so the response always reflects
  whatever the on-disk registry currently records (including peer
  engines that started after this one). Callers must still
  pid-check each entry before treating it as authoritative — an entry
  whose `processId` no longer exists, or exists but whose
  `Process.StartTime` disagrees with `processStartTimeUtc` beyond
  the tolerance, is a stale crash leftover. The primary consumer is
  the engine's own housekeeping sweep (runs on every graceful
  engine shutdown — see `### Housekeeping`); secondary consumers
  are observability surfaces — external ps-style listings
  listings, tree-view "other live engines on this machine" badges,
  diagnostic dumps. The engine never RPCs peer engines; the registry
  file is the only cross-engine channel.
- **`Engine.Shutdown(opts?)`** — explicit host-driven shutdown.
  Returns `{ accepted: true }` immediately, then drives the same
  graceful sequence the SIGTERM path runs:

  1. Emit `shutting-down` on `events` so subscribers can detach
     cleanly (same envelope shape as the idle-gate path emits).
  2. **Drain `rpc`.** In-flight handlers complete; new RPCs are
     refused with a discriminated `{ kind: "shutting-down" }`
     envelope (P2) so clients distinguish "engine refused,
     retry against a peer or wait for restart" from "pipe
     broke". The drain is capped at `opts.grace` (default
     2,000 ms) so a buggy handler cannot pin shutdown.
  3. Close all four pipes; passive observers see a clean EOF.
  4. Run the shutdown housekeeping sweep — remove this engine's
     entry from `engine-registry.json` and re-classify peer entries
     (see `### Housekeeping`), bounded by the same ≤ 1 s
     deadline that path already uses.
  5. Exit `0`.

  Options:

  ```
  {
    grace?:  number,  // ms to wait for in-flight rpc handlers, default 2000, hard cap 30000
    reason?: string   // ≤ 200 printable-ASCII chars (same shape rules as --instance-label)
  }
  ```

  `reason` is opaque to the engine and recorded on the final
  `engine.lifecycle` log line for postmortem reading ("vscode
  window closed", "user clicked Stop in tree view", "test
  teardown"); no semantic effect, no routing, never compared.
  Out-of-range `grace` is clamped to the hard cap with a warning
  log line; malformed `reason` (control chars, too long) is
  rejected with `{ kind: "schema-error" }`.

  **Authorization.** None at the protocol layer. Any client with
  a working `rpc` connection can issue `Engine.Shutdown` — this
  is intentional and matches the engine's scoping model. The
  engine is (workspace, launcher-instance)-scoped (P4); the
  `<instanceId>` segment of the endpoint is the authority
  boundary. A client that has the right endpoint was either
  spawned by the launcher or was handed the UUID by the launcher
  through a host-specific side channel, and is therefore already
  trusted to manipulate this engine's lifecycle. Adding a token
  on top of pipe-presence would be more wire shape with no actual
  security boundary to defend.

  **Idempotency.** Concurrent `Engine.Shutdown` calls land in
  the same in-flight drain; the second call gets
  `{ accepted: true }` and rides the in-progress shutdown to
  completion. There is no "already shutting down" error envelope —
  redundant calls are effectively no-ops.

  **Companion to `--idle-timeout 0` and `--parent-pid`.**
  `Engine.Shutdown` is the explicit host-driven exit path that
  the `--idle-timeout 0` mode relies on: a long-lived host that
  has turned off the idle gate uses the RPC to stop the engine
  cleanly when its own window closes, and falls back on the
  `--parent-pid` watchdog (or SIGTERM) only for crash paths
  where it never got the chance to issue the RPC.
- **`Config.*`** — `Get`, `Subscribe`, `ToggleFile`, `ToggleRule`.
  The VS Code extension is the primary writer (UI toggles); other
  clients are typically subscribers. The engine is the only authority
  for what is enabled / disabled. Clients do not issue batch RPC —
  N back-to-back `Config.Toggle*` calls are coalesced server-side
  into one snapshot swap and one fan-out envelope (see
  [Batching policy](#batching-policy)).
- **`Instructions.*`** — `List`, `Get(name)`, `GetAll`,
  `GetAlwaysAttached`, `GetRaw(name, opts?)`, `SearchContent(query, opts?)`,
  `SearchByMetadata(predicate?, opts?)`, `Subscribe`. `List` returns
  identity rows; `Get` / `GetAll` /
  `GetAlwaysAttached` return **projected** bodies (disabled rules
  filtered out, `[INSTxxxx]` tags preserved as cross-reference
  anchors, the highest-precedence workspace override preferred over
  bundled); `SearchContent` searches the projected index;
  `SearchByMetadata` filters the identity rows by a field predicate
  (case-insensitive regex over the string fields, coarse `applyTo`
  extension intersection, boolean / numeric equality, and per-section
  `sections.*` AND-intersection reported as `matchedAnchors`), returning
  a discriminated `ok` / `error` envelope so an invalid predicate comes
  back as structured feedback rather than an empty result; `GetRaw`
  returns the **source-faithful** bytes of the on-disk markdown file;
  `Subscribe` notifies on corpus reload. Overrides resolve against the
  `engine.instructions.overridesRoots` roots in precedence order
  (default `.github`): the engine watches each root's `instructions/`
  subfolder for `<name>.instructions.md` and the first root that
  supplies a file wins, falling back to the bundled corpus when none
  do.

  **`List(opts?)`** is the listing RPC — every other identity-shaped
  consumer (tree views, the `list_autocontext_instructions_files` LM
  tool, `search_autocontext_instructions_files_by_metadata`,
  `Discovery.*` index building) reads from it. Each entry carries:

  ```
  {
    key:            string,            // file basename, e.g. "dotnet-async-await"
    fileName:       string,            // "<key>.instructions.md"
    name:           string,            // frontmatter `name` ("<key> (vX.Y.Z)")
    version:        string,            // "X.Y.Z" parsed from name
    description:    string,            // frontmatter `description`
    applyTo?:       string,            // raw glob string (omitted if absent)
    hasChangelog:   boolean,           // sibling `<key>.CHANGELOG.md` exists
    contentHash:    string,            // "sha256:<hex>" over post-frontmatter body
    alwaysAttached: boolean,           // catalog-declared in `instructions-catalog.json`'s `alwaysAttached[]`
    label?:         string,            // curatorial label from `instructions-catalog.json` (omitted if none)
    categories:     string[],          // catalog membership names; resolve via `Instructions.Categories`
    disabled:       boolean,           // engine-resolved against `.autocontext.json`'s `disabled` flag
    source:         "bundled"|"override",
    overridePath?:  string,            // workspace-relative when source="override"
    sections?:      Array<{ heading: string, anchor: string, parent?: string }>
  }
  ```

  Bodies are **never** in `List` — the tree-view bulk render would
  otherwise pull every body for nothing. `opts.includeSections` defaults
  to `true` (the LM-tool / discovery paths need them); tree-view callers
  pass `false` to drop the section payload. The section shape
  intentionally matches the `instructions-manifest.json`
  generator output (`heading`, `anchor`, `parent?` — no `level`; the
  parent chain carries hierarchy).

  **`List` includes every bundled and override file** — disabled
  entries appear with `disabled: true` so the tree view can render
  the toggle UI, and the `alwaysAttached` flag lets the tree badge
  the meta-files distinctly. **`GetAll` filters out disabled files**
  unconditionally — it is the bulk-read path for tree-view rendering
  and CLI dumps; consumers that need disabled identity read `List`.

  **`Get(name)`** is a discriminated response, not a nullable one:

  ```
  type GetResponse =
    | { kind: "ok",        name, key, fileName, content, returnedSections, ... }
    | { kind: "disabled",  name, key }     // identity only — no description, no body
    | { kind: "not-found", name }          // name not in the corpus at all
  ```

  The `kind: "disabled"` envelope is **the** reason `Get` is not just
  a nullable string. LM tools (Copilot's
  `get_autocontext_instructions_file`) need to tell the model
  "this rule exists but the user has muted it" without leaking the
  body, the description, or the version — otherwise the model could
  quote the muted rule back from the description and route around
  the user's choice. `not-found` is a strictly distinct outcome
  (the name was never in the corpus, no user policy involved) and
  collapsing the two would make every "missing" response
  indistinguishable from "actively suppressed". Hooks consuming
  through `GetAlwaysAttached` and sub-agent materialisation paths
  treat `kind: "disabled"` as omission — the engine never returns
  a disabled identity envelope through `GetAlwaysAttached` because
  those consumption surfaces have no UI to render the muted state
  back to the user; only LM-facing surfaces with a model in the
  loop get the identity envelope.

  **`GetAlwaysAttached`** is the SessionStart / `PreCompact`
  consumer: it returns *only* the non-disabled files the catalog's
  `alwaysAttached[]` array declares, in deterministic
  order. The set is a declarative signal in the hand-authored
  `instructions-catalog.json` — today only
  `copilot.instructions.md` and `autocontext.instructions.md`
  are listed (they introduce AutoContext itself and must apply to
  every turn). Files with no `applyTo` but not in the
  `alwaysAttached[]` array (`code-review`, `design-principles`, `git-commit`,
  `rest-api-design`) are domain-conditional, not universal, and
  surface only via `Discovery.RouteForPrompt`.

  **`SearchContent(query, opts?)`** is the engine-owned content
  search backing `search_autocontext_instructions_files_by_content`
  and any future external content-search client. Today's
  TypeScript implementation reads every projected body to build a
  client-side trigram / inverted index on every cold start; moving
  the index into the engine (a) eliminates that startup cost,
  (b) keeps the index hot across queries, (c) tracks invalidation
  naturally via `Instructions.Subscribe` and the corpus reload
  revision counter, and (d) gives every other client — CLI,
  future JetBrains / Neovim shells — the same search without each
  re-implementing it. The response shape matches today's LM-tool
  output:

  ```
  Array<{
    name:        string, key: string, fileName: string, description: string,
    score:       number,
    excerpts:    Array<{ anchor: string, snippet: string, line?: number }>
  }>
  ```

  Disabled files are excluded from search by default (`opts.includeDisabled=false`);
  the LM tool flips this on only when explicitly asked to surface
  disabled guidance. The engine never indexes raw frontmatter or
  `[INSTxxxx]` tag noise — indexing runs on the same projected body
  that `Get` returns.

  **`GetRaw(name, opts?)`** returns the unmodified bytes of the
  on-disk markdown file — YAML frontmatter intact, `[INSTxxxx]`
  tags intact — with no disabled-state filter and no
  `alwaysAttached` filter. It exists as a separate method from
  `Get` because some callers need byte alignment with the source
  file the projected body cannot provide. The motivating case is
  the **rule enable/disable CodeLens**: the extension renders one
  lens per `[INSTxxxx]` tag at the tag's source-file line so the
  user can toggle individual rules, and although the projected body
  preserves the tags, it drops frontmatter and filters disabled
  rules — so its line numbers no longer align with the source file
  the lens decorates.
  "Open instruction source" commands, the corpus service's
  internal override-vs-bundled equality check, export tooling,
  and future raw-dump CLI verbs use it for similar reasons.
  Override resolution is under explicit caller control via
  `opts.source: "bundled" | "override" | "active"`:

  - `"active"` (default) — returns the override if one exists,
    else the bundled file. Matches the projection rule the rest
    of the surface uses; appropriate for callers that just want
    "the same content the engine would project from".
  - `"bundled"` — returns the bundled file even when an override
    exists. Used by the corpus service's internal override-vs-bundled
    equality check, and by UI callers whose user has opened the
    bundled file specifically.
  - `"override"` — returns the override or `kind: "not-found"`.
    Used by UI callers whose user has opened the override file.

  Callers whose byte offsets must align with a *specific* on-disk
  file (CodeLens lens positions, "open instruction source", future
  inline editors) must pass `"bundled"` or `"override"` explicitly
  — the source they pass must match the file the user actually has
  open. Silently retargeting a `bundled`-opened document to
  override bytes (or vice versa) shifts every byte offset and
  attaches toggles to the wrong rule.

  Response is a discriminated envelope mirroring `Get`:
  `{ kind: "ok", name, key, source, content }` /
  `{ kind: "not-found", name }`. There is no `kind: "disabled"`
  branch — disabled state is irrelevant to a source-file read.

  **`applyTo` matching: coarse engine-side filter, fine client-side
  matcher.** `applyTo` is consumed by three call sites — the
  `_by_metadata` LM tool's `applyTo` clause, the content-search
  `applyTo` post-filter, and the `Discovery.Route*` extension index.
  The engine handles the **coarse** layer ("could this file's
  `applyTo` ever match anything in this workspace?") and the
  client handles the **fine** layer ("does this file's `applyTo`
  match the user's specific glob right now?"). The split lives where
  it does because there is no portable cross-host equivalent of
  VS Code's `vscode.languages.match` — a hand-rolled .NET glob
  matcher would drift from editor semantics in edge cases
  (case sensitivity, `**` greediness against `.gitignore`-excluded
  paths, brace-expansion subtleties) — so fine matching stays on
  whichever host already owns a native matcher.

  Engine side (coarse, once at corpus-load):

  - **Parse, don't match.** A small lexical pass inside the
    `instructions-manifest-gen` build generator (Issue #4's
    build-time tool, which writes
    `Resources/instructions-manifest.json` over the corpus while
    cross-validating the hand-authored
    `Resources/instructions-catalog.json`; see
    `## Distribution > Resource manifests`) splits comma-separated `applyTo` strings, trims
    whitespace, brace-expands `**/*.{cs,fs,vb}` into individual
    globs, and extracts the **extension set** as a derived index.
    This is structural parsing, not glob algebra — it must round-trip
    (the recomposed glob list equals the original `applyTo` modulo
    whitespace) and it must not attempt to canonicalise globs,
    simplify `**` patterns, or otherwise reason about what a glob
    means. The parse result is **engine-internal state** consumed by
    the coarse filter and by `Discovery.RouteForPrompt`'s extension
    index; it is **not** published on the `List` envelope. The
    `List` row carries only the raw `applyTo` string, which clients
    hand verbatim to their host-native fine matchers
    (`vscode.languages.match` in VS Code,
    `Microsoft.Extensions.FileSystemGlobbing` in the CLI, `minimatch`
    in hooks). Comma-splitting and brace-expansion are trivially
    re-derivable from the raw string when a client ever needs them.
  - **Filter by workspace extension set.** `Instructions.List(opts)`
    accepts `opts.applyToWorkspaceFilter: boolean` (default `true`).
    When `true`, the engine drops every row whose internally-computed
    extension set is disjoint from `Workspace.Detect.extensions` — a
    workspace with no C# projects never sees C# files surface
    through `List`. The set intersection is cheap (small sets),
    reuses indices the engine already maintains for `Discovery.*`,
    and is the same trick `RouteForPrompt` uses today.
  - **Caller-supplied hint** (`opts.applyToHint?: string`) further
    narrows the candidate set on the engine side by extension only
    — `applyToHint: "**/*.cs"` keeps only rows whose internal
    extension set includes `.cs`. Hint matching is deliberately
    extension-only on the engine; full glob matching is the
    client's job.
  - **Opt-out** (`applyToWorkspaceFilter: false`) is the explicit
    bypass for the tree view's "show all instructions" mode and
    for export. Always-attached files are exempt from the
    workspace filter regardless — they apply to every workspace by
    definition.

  Client side (fine, per query):

  | Host | Fine matcher |
  |---|---|
  | VS Code extension | `vscode.workspace.findFiles` + `vscode.languages.match` (today's `InstructionsFilesLmToolsApplyToMatcher`, unchanged). Mirrors how `chatInstructions` decides which files attach. |
  | Hook scripts (Claude Code, VS Code Copilot) | `minimatch` for the extension-index lookup the hook already performs today; no glob × glob intersection needed in the hook surface. |
  | Other .NET clients | `Microsoft.Extensions.FileSystemGlobbing` against CWD with a 50-path cap (the same cap today's matcher uses for `findFiles`). |

  Both sides reading the raw `applyTo` string — the engine for
  internal coarse-filter derivation, the client for fine matching
  — guarantees the engine never excludes a row the client's fine
  matcher would have included: the coarse filter is a strict
  superset.
- **`Workspace.*`** — `Detect`, `Info`. Workspace-context detection,
  framework / language flags, override file inventory.

  **`Detect` return shape.** A flat record of named boolean flags
  (~60 today) plus two derived index fields the engine builds from
  the same source rules:

  ```
  {
    flags: {
      hasDotNet: boolean, hasCSharp: boolean, hasFSharp: boolean,
      hasVbNet: boolean, hasBlazor: boolean, hasRazor: boolean,
      hasXaml: boolean, hasWebForms: boolean,
      hasJavaScript: boolean, hasTypeScript: boolean,
      hasNodeJs: boolean, hasReact: boolean, hasAngular: boolean,
      hasVue: boolean, hasSvelte: boolean, hasNextJs: boolean,
      hasVitest: boolean, hasJest: boolean, hasJasmine: boolean,
      hasMocha: boolean, hasPlaywright: boolean, hasCypress: boolean,
      hasPython: boolean, hasJava: boolean, hasKotlin: boolean,
      hasScala: boolean, hasGroovy: boolean, hasC: boolean,
      hasCpp: boolean, hasRust: boolean, hasGo: boolean,
      hasSwift: boolean, hasRuby: boolean, hasPhp: boolean,
      hasLua: boolean, hasDart: boolean,
      hasHtml: boolean, hasCss: boolean, hasYaml: boolean,
      hasPowerShell: boolean, hasBash: boolean, hasBatch: boolean,
      hasDocker: boolean, hasUnity: boolean,
      hasAspNetCore: boolean, hasMaui: boolean, hasWpf: boolean,
      hasWinForms: boolean, hasEntityFrameworkCore: boolean,
      hasDapper: boolean, hasMediatR: boolean, hasSignalR: boolean,
      hasGrpc: boolean, hasGraphql: boolean,
      hasXunit: boolean, hasMsTest: boolean, hasNUnit: boolean,
      hasMongoDb: boolean, hasMySql: boolean, hasOracle: boolean,
      hasPostgres: boolean, hasSqlite: boolean, hasSqlServer: boolean,
      hasRedis: boolean
      // …authoritative list lives in the engine's detection rule
      // table, this enumeration is the public contract.
    },
    extensions: string[]           // union of all extensions the
                                   // active flags imply (e.g. hasCSharp → ".cs",
                                   // hasDotNet → ".csproj/.fsproj/.vbproj/.sln/.slnx")
  }
  ```

  Note: the `.github/instructions/` override inventory is **not**
  part of `Workspace.Detect`. Workspace shape (what frameworks /
  languages are present) and instruction-corpus content (which
  user overrides shadow bundled files) are independent concerns
  with independent change cadences and independent watchers. The
  override inventory is reachable via `Instructions.List`, which
  already projects bundled-vs-override per row.

  Each flag has a single deterministic source rule — either a glob
  set (e.g. `hasCSharp` is true iff at least one `**/*.csproj` exists)
  or a content pattern over a manifest file (e.g. `hasReact` is true
  iff `package.json` contains `"react":`). Activation rules add
  parent flags transitively: `hasNextJs` activates `hasReact`, which
  activates `hasNodeJs`. The full rule table is engine-internal but
  the schema above is the wire contract — new flags are additive,
  flag removal is a breaking change.

  The `extensions` field is the **single source of truth** the
  engine's coarse `applyTo` filter intersects against (see the
  `applyTo` matching subsection under `Instructions.*` above). It
  is derived from the same glob rules that drive flag detection,
  so any new file-rule flag automatically extends the extension
  set without a second declaration. Content-rule flags
  (`hasReact`, `hasEntityFrameworkCore`, …) contribute no
  extensions; they exist for instruction files whose `applyTo`
  would never differentiate them by file extension anyway.

  **`Info`** returns engine-process metadata (workspace path,
  engine version, `(instanceId, revision)` pair, idle-timeout
  state) for diagnostics; it does not duplicate the `Detect`
  payload.
- **`McpTools.*`** — `List`, `Invoke`. `List` surfaces the engine's
  MCP tool catalog (filtered by the same per-tool `disabled`
  state) for hosts that want to introspect what the
  engine would advertise to an MCP client.

  **`Invoke(name, arguments)`** is the pipe-RPC counterpart of MCP's
  `tools/call`. Pipe-side consumers — the VS Code extension's
  MCP Tools tree-view "play" button, any ad-hoc invoker that
  pipes JSON-RPC at the engine, integration tests, and any
  future hook script that
  wants to re-run a tool outside the agent loop — invoke MCP tools
  through this RPC rather than spinning up a parallel MCP-server-only
  process against the same workspace just to round-trip one
  `CallTool`. The MCP-server-only role stays the canonical model-facing
  transport; `Invoke` is the canonical non-model transport. Both
  surfaces share the same handler code (one implementation in
  `Engine.Core`); the difference is which process hosts it
  (daemon for `Invoke`, MCP-server-only role for `tools/call`).

  Response is a discriminated union mirroring the `Instructions.Get`
  shape (Issue #5 rationale):

  ```
  type InvokeResponse =
    | { kind: "ok",           name, content: ContentBlock[], isError?: false }
    | { kind: "tool-error",   name, content: ContentBlock[], isError: true }
    | { kind: "schema-error", name, errors: Array<{ path, message }> }
    | { kind: "disabled",     name }   // identity only — no result, no schema leaked
    | { kind: "not-found",    name }
  ```

  `content` matches MCP's `CallToolResult.content` block array
  verbatim so pipe and stdio surfaces serialise byte-identically.
  `tool-error` distinguishes "the tool ran and reported failure"
  from "the engine refused to dispatch" (`disabled` / `not-found`),
  same correctness rationale as `Get`'s envelope split. The engine
  validates `arguments` against the tool's `inputSchema` before
  dispatch and emits `schema-error` on mismatch — the same
  validation the MCP/stdio path performs, sharing one validator to
  avoid drift. Cancellation piggy-backs on the pipe-RPC framing's
  per-request token (no separate `Cancel` RPC); the engine forwards
  it to the worker, which honours it through the dispatch protocol's
  cancellation parameter. Worker dispatch is engine-internal:
  callers never see workers, never spawn them, and never know which
  worker handles which tool — the mapping lives in the embedded
  `mcp-tools-registry.json` the engine already owns (see the
  resource-manifest layout under `## Distribution` below).

  Out of scope for today, design constraint only: a future
  `McpTools.InvokeStream` sibling will mirror MCP's
  `notifications/progress`; `Invoke`'s shape must not preclude
  adding it. Schema exposure on the pipe is also out of scope here
  — it lands together with the meta-discovery forward-note below
  (`McpTools.GetDescription` / `SearchByMetadata` / `SearchByContent`).

  Forward-note (out of scope here, design constraint only): the same
  shape `Instructions.*` uses for instruction-file discovery — list /
  search-by-metadata / search-by-content / get — should eventually
  exist for the MCP tools themselves, as `McpTools.SearchByMetadata`
  / `McpTools.SearchByContent` / `McpTools.GetDescription` backing
  `mcp_tools_list` / `mcp_tools_search_*` / `mcp_tools_get` MCP tools
  (and their LM-tool shims). Categories and descriptions are already
  collected in the embedded registry; an `McpToolsContentIndex`
  mirror of `InstructionsFullTextSearchService` closes the symmetry. Today's
  `McpTools.List` envelope must therefore be designed so adding those
  siblings later is additive — leave room for `description`,
  `categories`, optional section-like metadata, and a stable `key`
  field per tool, even if the only consumer today is
  `Discovery.RouteForPrompt`.
- **`Discovery.*`** — `RouteForPrompt(prompt)`,
  `RouteForTool(toolName)`. `RouteForPrompt` powers the
  `UserPromptSubmit` hook's routing preamble and any future
  CLI / host that wants the same "what's relevant for this prompt"
  signal; `RouteForTool` powers the `PreToolUse` hook by mapping a
  tool identity to the instruction files whose `applyTo` would
  surface for that tool's domain (e.g. `analyze_csharp_code_style` →
  the C# coding-standards files). The engine builds two indices
  from already-owned state — *category → MCP tools* (inverted from
  each tool's `categories` array in `McpTools.List`) and *file
  extension → instruction files* (extracted from each instruction
  file's `applyTo` glob via `Instructions.List`) — and runs the
  same word-boundary literal scan (categories) plus
  `\.[A-Za-z][A-Za-z0-9]{0,12}` regex scan (extensions) the
  current `.cjs` does today. The `RouteForPrompt` response is
  `{ matchedCategories[], matchedExtensions[], tools[], instructions[] }`,
  filtered by the same disabled-state the engine applies elsewhere;
  `RouteForTool` returns the `instructions[]` slice keyed off the
  tool's declared category / extension affinity. Routing lives
  engine-side because the indices are already engine state,
  invalidation tracks `Instructions.Subscribe` /
  `Config.Subscribe` automatically, and the same RPC family can
  back any future prompt-routing debug client without
  duplicating the scan logic in TypeScript.

  **`RouteForFile(path)`** is the third member, keyed by a workspace
  file path rather than by prompt text or tool identity. It returns
  `{ instructions[], tools[] }`: the enabled instruction files whose
  `applyTo` covers the path (the same extension index, the same
  coarse match the `Instructions.List` `applyTo` filter applies),
  and the union of the MCP tools those files oblige — the
  `mcpTools` the build extracts from each file's
  `## MCP Tool Validation` section into `instructions-manifest.json`
  — minus any tool the workspace has disabled. It powers the
  `PostToolUse` verification role and the `Stop` backstop: the hook
  knows only that a file was written, and this is how it learns
  which checks that file owes. A path no file covers yields empty
  arrays, not an error (P2). Like its siblings it is a read over
  indices the engine already maintains; the obligations are corpus
  facts, so the index invalidates with `Instructions.Subscribe`,
  and the disabled filter with `Config.Subscribe`.

  Out of scope for `Discovery.*`: **host-specific tool registration**.
  VS Code Copilot's `vscode.lm.registerTool` LM tools are a registration
  surface specific to the VS Code extension host — Claude Code has no
  equivalent — and the engine does not own that registration. What the
  engine *does* back is every LM-tool's underlying handler (see the
  LM-tool surface section below); the engine simply doesn't care which
  hosts choose to register an LM-tool wrapper around it.

### LM-tool surface (host-specific registration, MCP-backed handlers)

Post-engine, the four instruction-discovery tools the VS Code
extension contributes today
(`list_autocontext_instructions_files`,
`search_autocontext_instructions_files_by_metadata`,
`search_autocontext_instructions_files_by_content`,
`get_autocontext_instructions_file`) are no longer extension-native.
The engine owns the only implementation — a single set of service
classes (`InstructionsManifestService`, `InstructionsFullTextSearchService`,
`InstructionsBodyProjector`) inside `Engine.Core` — and that
implementation is reachable through two parallel surfaces that run
in **two separate processes of the same engine binary**:

```
                  one implementation in Engine.Core
                                    ▲
                ┌───────────────────┴───────────────────┐
                │                                       │
          daemon role                          MCP-server-only role
     (autocontext-engine,                    (autocontext-engine
      no --mcp-server)                        --mcp-server with-stdio)
                │                                       │
        Engine pipe RPC                          MCP tools/call
        (Instructions.*)                         (instructions_*)
                │                                       │
        every pipe-RPC client:                   every MCP host:
        VS Code extension UI,                    Claude Code,
        agent plugin (hooks),                    Claude Desktop,
        VS Code LM-tool shims,                   Cursor, Inspector,
        ad-hoc scripts                           VS Code MCP manager
```

- **Engine pipe RPC (daemon role)** — `Instructions.List` /
  `SearchContent` / `Get` / `GetAlwaysAttached`, as specified
  above. Lowest latency, richest typed surface, consumed by every
  pipe-RPC client. In-memory state is kept fresh by the daemon's
  `FileSystemWatcher` → debounced reload pipeline, so reads do not
  hit disk on the hot path.
- **MCP `tools/call` (MCP-server-only role)** — exposes
  `list_instructions`, `search_instructions_by_metadata`,
  `search_instructions_by_content`, `get_instructions` as MCP tools
  over stdio, **always registered unconditionally**. Each MCP-tool
  handler instantiates the same service classes the daemon uses
  and answers from a **per-request** disk read of `.autocontext.json`
  plus the bundled side-car corpus from `AppContext.BaseDirectory`
  — no `FileSystemWatcher`, no long-lived in-memory cache, no
  cross-request state. Convergence with the daemon happens at the
  **disk layer**, not in shared memory: the daemon writes
  `.autocontext.json`, the MCP-server role observes the write on
  its next request.
- **VS Code LM-tool shims** — the extension keeps registering the
  four `vscode.lm.registerTool` entries it ships today, and each
  shim's `invoke` body dials the **daemon role's pipe RPC**
  (`Instructions.*` via `EngineDaemonManager`) on the engine the
  extension itself launched for its workspace. The MCP-server-only
  role is the model-facing transport for *external* MCP hosts; LM
  tools running inside the same VS Code window where the daemon
  engine is already alive take the shorter path. Byte-identical
  output between the LM-tool and MCP paths is guaranteed by the
  shared handler code in `Engine.Core`, not by routing one
  surface through the other.

**Double exposure is intentional, no suppression flag is needed.**
Inside VS Code Copilot the model sees both `#list_autocontext_instructions_files`
(first-class LM tool, never deferred, `#`-mentionable) and
`mcp_autocontext_list_instructions` (deferred MCP tool, reachable via
`tool_search`). Either path terminates at the same engine handler,
so the outcomes are identical. The LM tool exists solely to **escape
the deferred-tool discoverability tax** by promoting the discovery
surface into the always-available LM-tool list; the parallel MCP
variant is harmless because there is no semantic divergence between
the two paths to suppress. This is a deliberate inversion of the
original suppression-flag motivation, which assumed two surfaces
implied two possible behaviours.

Naming convention split (kept intentionally — three labels for one
handler):

| Surface | Tool names | Why this shape |
|---|---|---|
| Engine pipe RPC | `Instructions.List`, `Instructions.SearchContent`, `Instructions.Get`, `Instructions.GetAlwaysAttached` | Dotted, namespaced — matches the rest of the engine RPC vocabulary (`Config.*`, `Workspace.*`, `Discovery.*`, `McpTools.*`, `Agent.*`). |
| Engine MCP/stdio | `list_instructions`, `search_instructions_by_metadata`, `search_instructions_by_content`, `get_instructions` | snake_case, verb-first — consistent with the analyzer tools the engine already exposes (`analyze_csharp_code_style`, `read_editorconfig_rules`, …) and with the verb-first LM-tool names. |
| VS Code LM tools | `list_autocontext_instructions_files`, `search_autocontext_instructions_files_by_metadata`, `search_autocontext_instructions_files_by_content`, `get_autocontext_instructions_file` | Verb-first, fully self-describing — the LM-tool name is what the model sees in its tool list, so it reads like documentation. |

Breaking the LM-tool names would force migration of every
`copilot.instructions.md` reference and every existing user's mental
model of which tool to ask for; keeping all three name shapes is the
small blemish that buys consistency on each surface.
- **`Engine.Lifecycle`** — `Subscribe`. Streams engine-lifecycle
  events to every connected client: `started` (sent immediately on
  subscribe so clients always know the current
  `(instanceId, revision)` pair),
  `reloading` (config or corpus reload in progress),
  `reloaded` (post-reload, with the new revision so clients
  can invalidate caches), `shutting-down` (idle timeout fired or
  signal received, fixed grace period before the pipe closes).
  This is the authoritative channel for engine-owned lifecycle;
  clients respond by invalidating caches, refreshing UI, or
  flushing host-local materialisations. Contrast with
  `Config.Subscribe` and `Instructions.Subscribe`, which stream
  *content* deltas; `Engine.Lifecycle` streams *process*
  transitions.
- **`Agent.*`** — `SubagentStarted`, `SubagentStopped`,
  `Compacted`, `ToolUsed`, `TurnEnded` (all fire-and-forget
  notifications from a hook script to the engine), plus
  `Events.Subscribe` (server-streaming). The hook scripts are
  the only sensor the engine has on agent-loop transitions; these
  RPCs turn that sensor's readings into engine-broadcast signals
  every other client can observe. The VS Code extension
  subscribes to `Agent.Events` to render "active sub-agents"
  panels, "compaction in progress" status indicators, and
  per-session tool-usage hints in tree views — observability the
  chat surface alone does not provide. Notifications are
  best-effort: lost events are tolerable (the engine never makes
  *correctness* decisions from agent events; only UX
  enhancements). See the agent-plugin section above for the
  per-hook design rationale.
- **`Engine.WriteLog`** — fire-and-forget notification (no response,
  no ack) used by **workers** to forward `ILogger<T>` records into
  the engine's log files. The handler deserialises one record, routes
  it by `category` prefix (records matching `worker.<workerId>` /
  `worker.<workerId>.<Type>` are appended to
  `…\<workspaceHash>\<instanceId>\logs\worker-<workerId>.log`,
  everything else is appended to
  `…\<workspaceHash>\<instanceId>\logs\engine.log`), and fans the
  record out to every subscriber on the `logs` pipe and to any active
  `Logs.Tail*` RPC subscriber. Routing is by prefix alone; workers
  never specify a destination and the engine never asks. Both files
  are engine-owned per P5 — the engine is the sole writer of every
  file under `…\logs\`. Under normal operation workers never open
  their own log file under the per-instance subtree, never dial the
  `logs` pipe as a producer, and never write to stdout/stderr — the
  engine spawns workers detached with redirected/null stdio (same
  spawner discipline the engine itself is launched under, see the
  *Engine termination signal* pitfall); the engine supervises each
  worker via `Process.Start` and emits each captured stderr line
  as an engine-side log record under category
  `worker.<workerId>.engine.stderr` — which the prefix router lands
  in that worker's `worker-<workerId>.log` alongside the worker's
  own in-band records, so
  the rare write that bypasses the worker's logging provider is still
  observable. When the engine is briefly unreachable (engine
  mid-shutdown, RPC pipe unbound during a startup race, worker still
  inside cold-start), the worker buffers records in a bounded
  in-memory ring and replays on reconnect — see the *Worker–engine
  connectivity* pitfall for the loss semantics. Record shape:

  ```
  {
    timestamp:   string,           // ISO-8601 UTC, set by the worker at log time
    category:    string,           // dotted, prefix-groupable; see *Log categories*
    level:       "trace" | "debug" | "information" | "warning" | "error" | "critical",
    eventId?:    { id: number, name?: string },
    message:     string,           // formatted message body
    properties?: object,           // structured KV state (Microsoft.Extensions.Logging scope+state)
    exception?:  { type: string, message: string, stackTrace: string, inner?: object }
  }
  ```

  The shape is the canonical wire form for **every** record the
  engine emits on the `logs` pipe, whether the record originated
  from an engine-internal `ILogger<T>` call (where the engine fills
  the envelope directly) or from a worker's `Engine.WriteLog`
  notification (where the worker's `ILoggerProvider` fills it and
  ships it). Both paths terminate at the same sink (P1 — one
  handler, transport-agnostic). The notification is **best-effort**:
  the engine bounds its in-memory ingest buffer and drops oldest
  records on overflow, emitting a single synthetic record
  (`category: "engine.logging"`, `level: "warning"`,
  `message: "dropped N worker log records"`) the next time the
  buffer drains; workers never block on `WriteLog` and never await
  delivery. See the *Log pipeline backpressure* pitfall for the
  failure-mode discipline.
- **`Logs.*`** — `GetEngine(opts?)`, `TailEngine(opts?)`,
  `GetWorker(workerId, opts?)`, `TailWorker(workerId, opts?)`. The
  structured pipe-RPC surface for the per-instance log files the
  engine owns under `…\<workspaceHash>\<instanceId>\logs\`.
  `engine.log` and `worker-<workerId>.log` are written by the engine —
  directly for engine-emitted records, and via `Engine.WriteLog` for
  worker-emitted records routed to the right per-worker file by the
  record's `category` prefix (`worker.<workerId>.*` →
  `worker-<workerId>.log`; everything else → `engine.log`). One file
  per spawned worker; the file is created lazily on the worker's
  first record and rotated by the engine's in-process rotation
  logic (`--log-rotation` thresholds), so the active file tracks the
  current rotation window and rotated history sits beside it under
  retention (see `### Housekeeping`).

  `Get*` and `Tail*` return discriminated envelopes (P2):

  ```
  type GetResponse =
    | { kind: "ok",        records: LogRecord[], truncated: boolean }
    | { kind: "not-found", workerId: string }   // GetWorker / TailWorker only

  type TailFrame =
    | { kind: "ok",        record: LogRecord }                 // streamed per record
    | { kind: "not-found", workerId: string }                  // terminal, GetWorker / TailWorker only
    | { kind: "evicted",   reason: "slow-subscriber" }         // terminal, see backpressure rule below
  ```

  `not-found` distinguishes "this `workerId` is not a worker the
  current engine has ever spawned" from `kind: "ok"` with an empty
  `records` array ("a real worker that simply hasn't logged yet")
  — a CLI subcommand or tree-view tooltip needs to tell the two
  apart. `GetEngine` / `TailEngine` never return `not-found` (the
  engine's own log file always exists for the current process).

  `opts.lastN` caps from the tail (default unlimited, but practical
  clients pass a bound); `opts.since` filters by timestamp;
  `truncated: true` signals the file rolled past the requested
  range. `Tail*` server-streams new records as the engine appends
  them, replaying from `opts.since` if given (default: stream from
  connect-time onward). Same `LogRecord` envelope as the `logs` pipe
  and `Engine.WriteLog`:
  `{ timestamp, category, level, eventId?, message, properties?, exception? }`.

  `Get*` and `Tail*` exist as separate verbs so a tree-view
  "show last 200 lines" render does not have to deal with stream
  lifetime, and `Tail*` exists so the live-follow path does not have
  to poll. Two RPCs per file (engine, worker) keep the per-worker
  routing the engine already does on ingest visible at the RPC
  surface — the alternative ("one `Logs.Tail(filter)` with a
  category-prefix predicate") would push that routing into every
  client. Workers themselves never call `Logs.*`; the surface is
  read-only and engine-served.

  **Backpressure on `Tail*`.** Each `Logs.Tail*` subscriber inherits
  the same non-blocking fan-out discipline the `logs` pipe uses
  (see the *Log pipeline backpressure* pitfall): a per-subscriber
  bounded send buffer, and a subscriber that cannot drain in time
  is disconnected (its stream closed with a terminal
  `{ kind: "evicted", reason: "slow-subscriber" }` frame) rather
  than allowed to back-pressure the file sink, the ingest loop, or
  any other subscriber. The "losing a subscriber must never affect
  engine progress" contract applies regardless of whether the
  subscriber arrived via the raw `logs` pipe or via `Tail*` RPC.

  Relationship to the `logs` pipe: the `logs` pipe is the **passive,
  no-handshake, raw-NDJSON** debugging surface — `Get-Content -Wait`
  and `nc` work without a client library, and it streams every
  record the engine sees (engine and worker, distinguished by
  `category`). `Logs.*` is the **structured, handshake-required,
  per-file** surface — clients that already speak `rpc` (tree views,
  CLI subcommands, future hosts) use it to ask for one specific file
  with a request-shaped API and a bounded snapshot mode. Two
  surfaces, different audiences, identical record schema.
- **Future:** `Diagnostics.Run`, host-specific notification
  channels.

### Log categories

Every record on the `logs` pipe carries a `category` string that
identifies its origin. Categories are dotted, prefix-groupable
literals matching `Microsoft.Extensions.Logging` category
conventions — subscribers filter by prefix-match, not enum-equals,
so new producers can introduce new prefixes without bumping the
protocol. The taxonomy is **convention, not closed enum**: the wire
field stays `string`; documented prefixes are the recommendation
that keeps logs grep-friendly across hosts.

Current prefixes:

| Prefix | Producer | Typical sub-categories |
|---|---|---|
| `engine.rpc` | engine | per-RPC-handler `ILogger<T>` categories (e.g. `engine.rpc.Instructions.Get`) |
| `engine.events` | engine | `Engine.Lifecycle` broadcaster, `Agent.Events` fan-out |
| `engine.health` | engine | health-pipe accept/handshake trace |
| `engine.lifecycle` | engine | startup, shutdown, idle-timeout transitions |
| `engine.startup` | engine | argv parse, pipe bind, manifest load — anything before accepting connections |
| `engine.logging` | engine | the log pipeline's own diagnostics (buffer overflow drops, slow subscriber evictions) |
| `worker.<workerId>.engine.stderr` | engine | captured worker stderr forwarded by the engine's process-supervision channel (the rare worker write that bypasses the logging provider); routes into `worker-<workerId>.log` by the same `worker.<workerId>.*` prefix rule that handles in-band worker records |
| `worker.<workerId>` | worker | one prefix per worker (`worker.dotnet`, `worker.workspace`, `worker.web`); `<workerId>` matches the worker's `id` in `workers.json` |
| `worker.<workerId>.<Type>` | worker | per-type sub-categories under a worker (e.g. `worker.dotnet.RoslynAnalyzer`); free-form below the worker prefix |

Worker-side seam (composition contract): worker hosts register
`AddEngineLoggerProvider()` from `AutoContext.Workers.Core` during
startup.
That provider serialises every
`ILogger<T>` record into
the `Engine.WriteLog` notification with the worker's `id` baked
into the `category` prefix; the worker codebase itself never sees
the transport choice. Workers therefore use `ILogger<T>` exactly
as any other .NET service does, and the engine remains the single
owner of the on-disk log file and the wire log stream.

### Naming conventions

The engine exposes several distinct vocabularies on the wire and at
the seams — RPC method names, subscription event kinds, envelope
discriminators, JSON field names, endpoint kinds, MCP tool names, LM
tool names, CLI verbs, log categories, manifest filenames. Each
follows a fixed casing rule. The rules are chosen so a reader can
identify *what kind of name* a token is from its shape alone, and
so renaming any one of them is unambiguously a breaking-change
protocol event.

#### Casing rules (master table)

| Vocabulary | Casing | Examples |
|---|---|---|
| RPC method names (engine pipe RPC) | Dotted PascalCase: `Namespace.Method`, or `Namespace.Subnamespace.Method` | `Config.ToggleFile`, `Instructions.SearchContent`, `Engine.Lifecycle.Subscribe` |
| Subscription event-kind literals | **kebab-case** wire strings | `started`, `reloading`, `reloaded`, `shutting-down` |
| Discriminated-envelope `kind` literals (P2) | **kebab-case** wire strings | `ok`, `disabled`, `not-found`, `tool-error`, `schema-error`, `shutting-down`, `evicted` |
| JSON field names (requests, responses, envelope payloads) | camelCase | `instanceId`, `workspaceHash`, `revision`, `isError`, `applyTo`, `contentHash` |
| Endpoint kinds | lowercase, no separators | `rpc`, `events`, `health`, `logs` |
| MCP tool names (stdio surface) | snake_case, verb-first verb-noun pair | `list_instructions`, `analyze_csharp_code_style`, `read_editorconfig_rules` |
| VS Code LM tool names | snake_case, verb-first, fully self-describing | `list_autocontext_instructions_files`, `get_autocontext_instructions_file` |
| CLI verbs | lowercase, space-separated `noun verb [args]` | `instructions list`, `config toggle`, `workspace info`, `engine logs` |
| Log-category prefixes | Dotted; lowercase namespace, PascalCase tail when the tail mirrors an RPC name | `engine.rpc.Instructions.Get`, `engine.lifecycle`, `worker.dotnet.RoslynAnalyzer` |
| Resource manifest filenames | kebab-case `.json` | `instructions-catalog.json`, `instructions-manifest.json`, `mcp-tools-catalog.json`, `mcp-tools-registry.json`, `workers.json` |
| .NET internal classes / services | PascalCase (standard .NET identifier rules) | `ConfigFileService`, `InstructionsManifestService`, `WorkspaceContextDetector` |
| Placeholder tokens in this doc | `<lowerCamelCase>` inside angle brackets | `<workspaceHash>`, `<instanceId>`, `<name>`, `<workerId>` — see [Identifier tokens](#identifier-tokens) |

#### Cross-cutting rules

- **One handler, up to four name shapes.** A capability has at most
  four names — one per surface. For "list the instructions" that's
  `Instructions.List` (RPC) ↔ `instructions list` (CLI) ↔
  `list_instructions` (MCP) ↔ `list_autocontext_instructions_files`
  (LM tool). All terminate at the same engine handler (P1); the
  shape difference is per-surface convention, not a behavioural
  distinction. See [Naming convention split](#lm-tool-surface-host-specific-registration-mcp-backed-handlers)
  for the LM-tool / MCP / pipe-RPC table.
- **PascalCase ↔ "thing you invoke"; lower-case ↔ "value the wire
  emits".** A token that begins with an upper-case letter is an RPC
  method name, a notification name, or a .NET identifier —
  something with code behind it. A token that begins with a
  lower-case letter is a wire literal — an event kind, an envelope
  discriminator, a CLI noun, a log-prefix segment.
- **Multi-word wire literals are kebab-case, never camelCase.**
  Event kinds and envelope `kind` values are the same wire-literal
  family; both use kebab-case for compounds (`shutting-down`,
  `tool-error`, `not-found`). JSON *field* names stay camelCase
  (`instanceId`, `isError`); the casing distinction marks "value"
  vs "field" at a glance and prevents a renamer from accidentally
  treating one as the other.
- **RPC names do not appear in payload literals, and vice versa.**
  `Engine.Lifecycle.reloaded` in prose is shorthand for "the
  `reloaded` event kind emitted on the `Engine.Lifecycle.Subscribe`
  stream"; the wire frame contains the RPC name once (on subscribe)
  and the discriminator value once (per event), never a method
  literally called `reloaded` on `Engine.Lifecycle`.
- **CLI ↔ RPC ↔ MCP mapping is mostly mechanical.** A new RPC
  `<Namespace>.<Method>` typically implies the matching CLI verb
  (`<namespace> <method-kebab>`) and MCP tool
  (`<namespace>_<method_snake>`). The CLI is allowed to collapse
  RPC variants behind one verb when the variants differ only by
  argument shape — `Config.ToggleFile` and `Config.ToggleRule` both
  surface as `config toggle <file> [<ruleId>]`, with the rule form
  selected by the presence of the positional `<ruleId>`. The MCP
  and pipe-RPC surfaces never collapse this way; each variant keeps
  its own name. Other documented exceptions (e.g. `Workspace.Info`
  has no MCP equivalent) are noted at the variant's definition
  site, not the default.
- **Wire literals are stable; renaming is a protocol break.** Event
  kinds, envelope `kind` values, endpoint kinds, MCP tool names, and
  CLI verbs are part of the contract subscribers depend on. Any
  change is a breaking-change version bump.

### Identifier tokens

- **`<name>`** in `Instructions.{Get,GetRaw,Subscribe}` is the bundled
  file's stem (filename without `.instructions.md`), case-sensitive
  on POSIX, case-preserving on Windows. Override resolution looks for
  `<workspace>/.github/instructions/<name>.instructions.md` and
  prefers the override over the bundled source byte-for-byte.
- **`<workspaceHash>`** is `sha256(normalisedWorkspacePath):0..16`
  rendered as uppercase hex (`[0-9A-F]{16}`) — the same prefix used
  in the endpoint. It identifies the
  *workspace*; on its own it is not sufficient to address any
  on-disk artefact, because every artefact is scoped to a
  (workspace, launcher-instance) pair.
- **`<instanceId>`** is the launcher-minted UUIDv4 passed on
  `--instance-id`, **fresh on every spawn** (P4 — launchers must
  never reuse a UUID across respawns). It appears as the
  `#<instanceId>` suffix of every endpoint (one UUID, four endpoints
  sharing it within a launcher) **and** as a path segment in every
  per-instance on-disk artefact: engine logs and client caches all
  live under `…\autocontext\<workspaceHash>\<instanceId>\`
  (Windows; POSIX equivalent under the OS user-cache root). Two
  launchers on the same workspace therefore get disjoint on-disk
  subtrees — they cannot interleave each other's log lines, and a
  hook running under one launcher cannot read or corrupt cache
  files written by a hook under the other.

## Authority model: engine owns, clients cache

The engine is the single owner of every piece of AutoContext state
for a workspace — config, instructions corpus, projection,
workspace-context detection, MCP tool catalog, worker lifecycle.
Clients (VS Code extension, Anthropic plugin, any other pipe-RPC
consumer) are
**caches with UI**, never authorities. The contract is one-way:

- **Reads go through the engine.** Even if a client has a local
  cache for a host-specific reason (an Anthropic sub-agent file
  path, a future tool that demands a `Uri`), the cache is
  derived from an engine RPC, not from disk inspection or
  re-projection.
- **Writes go through the engine.** `Config.ToggleFile` /
  `Config.ToggleRule` are RPCs; clients never edit
  `.autocontext.json` directly. The engine validates,
  persists, and broadcasts the change.
- **Cache invalidation is engine-driven.** Clients learn that
  their caches are stale by subscribing to engine events, not by
  polling, watching files, or rebuilding projection on their
  side. The relevant event channels are `Config.Subscribe`,
  `Instructions.Subscribe`, and `Engine.Lifecycle.Subscribe`.
  Together they cover content changes and process transitions;
  there is no fourth channel a client needs to invent.
- **Lifecycle events are first-class.** A client must subscribe
  to `Engine.Lifecycle` early (right after `Engine.Hello`) and
  treat its events as authoritative process-state transitions.
  In particular:
  - On `started` / revision change, the client invalidates
    every host-local cache (the engine may have hot-reloaded
    config or restarted; a fresh `instanceId` distinguishes a
    restart from an in-process reload, and the revision orders
    snapshots within one engine instance).
  - On `reloading`, the client may show a transient "refreshing"
    UI affordance but **must not** issue redundant content RPCs
    — the matching `reloaded` event will arrive with the new
    revision.
  - On `shutting-down`, the client stops accepting user actions
    that would issue writes (toggles, override exports), drains
    any in-flight reads, and treats subsequent connect failures
    as "engine restarting, retry under the cold-start protocol"
    rather than "engine crashed".

Client cache cleanup is a client concern. The engine emits
lifecycle events and content-change subscriptions; what a client
materialises to disk and how it cleans up after itself is the
client's contract with its host (VS Code's extension storage,
Anthropic's session lifecycle, the OS user-cache directory). The
engine itself caches *only* in-memory state that invalidates on
internal events — no engine-owned on-disk artefact ever needs an
external cleanup actor, by design.

## Projection ownership

The engine is the **only** writer of projected instruction state.
**All projection happens in-memory**, on every read, from the
workspace's `.autocontext.json` plus the raw corpus — there is no
on-disk projection artefact at all. `Instructions.Get` returns the
projected body as a string over the pipe. This eliminates:

- The `<extensionPath>/instructions/.generated/` shared folder.
- Per-workspace `.workspaces/` projection output directories and the
  metadata generator that wrote them.
- The cross-window / cross-host lock-file dance.
- The read-only-mount problem on Claude plugin installs.

The only on-disk artefacts under `instructions/` are the source
markdown files (`*.instructions.md`) and any user overrides at
`<workspace>/.github/instructions/`.

Hosts that need a file path get one of two patterns:

- **VS Code extension:** does not need a file path. Instructions
  reach the chat surface through the agent-plugin SessionStart
  hook (see below), not through any VS Code `chatInstructions`
  declaration — and that hook fires whether the host is Claude
  Code or VS Code Copilot. The extension is a pure RPC consumer
  — tree views, decorations, hovers, and previews all consume
  projected bodies as strings from `Instructions.Get` /
  `Instructions.GetAll`. No projection cache, no static-path
  mirror, no on-disk artefact under `<extensionPath>`. Commands
  that open an instruction *source* in the editor open the
  bundled file at `<extensionPath>/engine/Instructions/...`
  or the workspace override at
  `<workspace>/.github/instructions/...` — neither is a
  projected body, so neither requires a cache.
- **Agent-plugin SessionStart hook:** calls `Instructions.GetAlwaysAttached`
  and returns the bodies inline as `additionalContext`. The set is
  small by design (2 files today: `copilot` and `autocontext`) and
  curated by the catalog's `alwaysAttached[]` array in
  `instructions-catalog.json` — not by `applyTo` absence. No file ever gets
  written under `${CLAUDE_PLUGIN_ROOT}`. Sub-agents that need
  file paths materialise them under the per-instance cache root
  (`%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\cache\`
  on Windows,
  `$XDG_CACHE_HOME/autocontext/<workspaceHash>/<instanceId>/cache/`
  or `~/.cache/autocontext/<workspaceHash>/<instanceId>/cache/`
  on POSIX). The hook owns this cache: SessionStart writes,
  SessionEnd cleans, and the engine never reads or writes those
  paths.

General rule for any future client cache: write under the
per-instance cache root
(`%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\cache\<client>\`
on Windows,
`$XDG_CACHE_HOME/autocontext/<workspaceHash>/<instanceId>/cache/<client>/`
or `~/.cache/autocontext/<workspaceHash>/<instanceId>/cache/<client>/`
on POSIX), never under the host's install directory
(`<extensionPath>`, `${CLAUDE_PLUGIN_ROOT}`). Install directories
are read-only on managed installs and get wiped on host upgrade;
the OS cache root is writable, survives host upgrades, and gives
every client one consistent place to find and clean its
launcher-instance-scoped artefacts. The per-instance segments
(`<workspaceHash>\<instanceId>`) are what isolate one launcher's
on-disk state from another's — two VS Code windows open on the
same workspace get disjoint cache subtrees, so a sub-agent hook
in one window cannot stomp materialised files belonging to a
sub-agent in the other. Engine logs live as a sibling `logs\`
directory under the same per-instance root (see P4 / P5 below),
so a postmortem reader who has the launcher's UUID can find every
artefact that engine produced in one place.

## Sharing principle (overarching)

**The engine is .NET; hosts are clients.** All projection, config,
and instruction-corpus logic lives in **one** place — the engine
binary, sourced from `AutoContext.Engine/` — written in C#. Every
host (VS Code extension, Anthropic plugin, future JetBrains /
Neovim shells, debug or scripting clients) is a *client* of the
engine. Sharing
happens at the **wire-protocol** level (named-pipe RPC), not at the
source-code level.

Consequences:

- **One implementation, one home.** `ConfigFileService`,
  `InstructionsBodyProjector`, `InstructionsCorpusReader`,
  `InstructionsManifestService`, the engine's hosted services, and
  every RPC handler all live in `AutoContext.Engine/`. The engine
  binary is the only producer.
- **The VS Code extension keeps no co-projector.** Once the engine
  ships, the extension's TS-side `AutoContextConfigManager`,
  `InstructionsFilesManager`, `InstructionsFileContentProjector`,
  `LogServer`, `HealthMonitorServer`, `WorkerControlServer`,
  `AutoContextConfigServer`, and any in-process projection code are
  *deleted*. The extension's remaining responsibility is wiring
  `EngineDaemonManager` (TS) to its tree views, codelens providers, and
  decoration providers. No on-disk projection cache lives in the
  extension — the agent-plugin hooks handle chat-side instruction
  delivery (under whichever hook host is running, including
  VS Code Copilot in the same window).
- **`EngineDaemonManager` is the only shared TS class.** Lives in
  `Nodejs.Core/src/engine/`. Owns engine-daemon lifecycle from the
  TS side (find-or-spawn against the bundled `autocontext-engine`
  binary, supervise, tear down on host shutdown) and exposes the
  engine's pipe-RPC surface as typed methods on top of that
  lifecycle. Used by the VS Code extension and by the agent-plugin
  `.cjs` hook scripts (under whichever hook host runs them). Speaks
  the same wire protocol the engine serves.
- **No invented cross-host seams.** This is *not* a ban on .NET DI.
  It is a ban on inventing portability interfaces (`IFileSystem`,
  `IWorkspace`, a custom `IHostEnvironment`-shaped wrapper) just to
  pretend the C# engine and the TS extension share code. They do
  not share code; they share a wire protocol. Inside the engine, use
  `Microsoft.Extensions.Hosting.IHostEnvironment`, `ILogger<T>`,
  `IOptions<T>`, and `IConfiguration` exactly as the rest of the
  .NET solution does. New interfaces only appear when a *second
  concrete* implementation is being added now — not hypothetically
  later.
- **Duplication is the lesser evil vs. abstraction.** A few lines
  repeated between the C# engine and a hypothetical second .NET
  host are fine. An interface invented to deduplicate them is not.
- **Shells stay thin.** `AutoContext.CommandLine` and `AutoContext.VsCode`
  contain almost nothing but: arg / activation parsing, host-specific
  surfaces (vscode UI, CLI argv), the `EngineDaemonManager` plumbing, and
  the run / teardown loop. Logic that is not host-specific belongs
  in the engine.

## Design principles (cross-cutting)

Each per-feature section above ends up restating the same handful of
invariants. Hoisting them here so the implementation has one place to
check against, and so future capabilities inherit the rules instead
of re-deriving them.

These are **architectural guidelines, not implementation
specifications** — they constrain *what shapes are allowed*, not
*which classes hold which methods*. Specific service names,
namespaces, file layouts, and DI wiring are the implementation's
choice; conformance with the principles below is non-negotiable.

### P1. One handler per capability; transports are marshalling shims

Every capability (instructions discovery, MCP-tool invocation, config
read/write, workspace detection, prompt routing, agent-event
broadcast) has **one** engine-side service implementation. Pipe RPC,
MCP/stdio, and host-specific LM-tool registrations are
deserialise → call-the-one-handler → serialise shims with no business
logic of their own.

- A new transport never adds capability; a removed transport never
  removes it.
- A field that can't be expressed on the engine RPC envelope can't be
  expressed on any transport.
- Validation rule: a recorded response on transport A diffs cleanly
  against transport B for the same input. The pipe `McpTools.Invoke`
  response and the MCP/stdio `tools/call` response must produce zero
  diff for `content`; the LM-tool `get_autocontext_instructions_file`
  result and the MCP `get_instructions` result must produce zero
  diff for the projected body.
- The named instances of this principle in this doc — instruction-discovery
  shims, `McpTools.Invoke` shim — are illustrations, not exhaustions.

### P2. Discriminated envelopes for state-bearing reads

Any RPC whose answer can be "the thing exists but the user has muted
it" returns a discriminated envelope (`ok` / `disabled` / `not-found`
/ `*-error`), never a nullable. `Instructions.Get` and `McpTools.Invoke`
are the seed cases. Every future state-bearing read inherits the
shape:

- `not-found` and `disabled` are strictly distinct outcomes and must
  not be collapsed by any client or transport.
- LM-facing surfaces (model in the loop) surface `disabled`
  verbatim so the model can tell the user what's muted; consumption
  surfaces with no UI (`GetAlwaysAttached`, sub-agent materialisation,
  `additionalContext` injection) treat both outcomes as omission. The
  envelope shape stays the same; the *consumption rule* differs by
  surface.
- Identity-only envelopes (`disabled`, `not-found`) leak nothing
  beyond the queried name — no description, no version, no schema —
  so a model cannot route around the user's mute by reading the
  metadata.

### P3. Three decoupled representations — disk, engine-internal, wire

A capability's data has up to three representations, and **none
dictates another's shape**:

1. **On-disk** (authoring / generation) — optimised for how the data
   is produced. The curatorial layer is hand-authored
   (`instructions-catalog.json`); the per-file facts are
   build-generated (`instructions-manifest.json`). These two files
   are deliberately *different* shapes serving *different* producers
   (a human editor vs. a build tool), not two copies of one schema.
2. **Engine-internal** (runtime model) — the immutable in-memory
   snapshot the engine merges from the on-disk files at startup
   (`InstructionsManifestSnapshot`: categories + per-file domain
   objects, including engine-only fields like `activationFlags` and
   the parsed `applyTo` extension set). Optimised for projection and
   indexing, free to hold derived structure the wire never sees.
3. **Wire** (per-RPC projection) — what an envelope returns, projected
   per request from the snapshot against workspace state
   (`Instructions.List` rows, `Instructions.Categories` taxonomy).
   Optimised for the consumer; carries `disabled` / `source` /
   `overridePath?` resolved per request, omits engine-only fields.

- Don't fuse the layers. The original defect this principle corrects
  was making one on-disk file "the wire shape" — which silently
  dropped the curatorial taxonomy and forced disk format to follow
  protocol shape.
- Don't publish derived structure on the wire just because the engine
  derived it. Every published field is a field future engine versions
  must keep producing.
- The parsed `applyTo` extension set is the canonical engine-only
  field: the engine needs it for its coarse filter and
  `Discovery.RouteForPrompt` index, but it lives only in the
  generated `instructions-manifest.json` and the runtime snapshot and
  never appears on `Instructions.List`. Clients re-derive it from the
  raw `applyTo` string trivially when (if) they need it.
- `activationFlags` is likewise engine-only: the catalog carries it on
  disk, the snapshot evaluates it against workspace state, and only
  the resulting `disabled` boolean ever reaches the wire.

### P4. Workspace identity is one hash; engine identity adds one UUID

`<workspaceHash> = sha256(normalisedWorkspacePath):0..16` is **the**
workspace identifier, rendered as **uppercase** hex (`[0-9A-F]{16}`).
Path normalisation is **surface-form only**: uppercase on Windows,
trim trailing separators. Symlinks, junctions, drive substitutions,
and 8.3 short names are deliberately **not** resolved. Hashing
happens once on the result.

**Why surface-form, not resolved.** Resolving symlinks correctly
requires a file-system syscall (`realpath` /
`GetFinalPathNameByHandle`) on every endpoint composition, by every
participant — the engine when it binds, every CLI invocation when
it dials, every hook script when it constructs an endpoint. That
syscall costs real I/O on a hot path that today is pure string
work, and it introduces failure modes the string path doesn't have
(dangling symlinks, ACL-blocked path components, network-share
timeouts, OneDrive placeholder rehydration) — any of which would
turn endpoint composition from infallible into fallible and block
spawn / dial entirely. In exchange, resolution would collapse the
rare case where the same user opens the same workspace through two
different surface paths in two different launcher sessions, where
the failure mode without resolution is annoying-but-recoverable
(two engines watching the same disk; the user picks one canonical
path and the duplicate goes away on next launch). The Registry
layer's `RegistryFileService.ComposeMutexName` made the same call
for the same reason; diverging in `WorkspaceHash` would create
cross-component drift (registry says "two workspaces", endpoint
says "one workspace") which is worse than either consistent
choice. The trade-off is captured in code in
`WorkspaceHash.Normalise`'s `<remarks>`.

Engine
identity adds **one** launcher dimension on top — `<instanceId>`,
a UUIDv4 the launcher mints **fresh on every spawn** (every
`Process.Start` / `child_process.spawn` invocation of
`autocontext-engine`) and passes verbatim on `--instance-id`.
Launchers MUST NOT reuse an `<instanceId>` across respawns;
treating it as per-launch is what guarantees the registry remains
append-only, the housekeeping sweep stays simple, and endpoint
collisions are launcher bugs rather than expected shapes the
engine has to be idempotent against.

Endpoint names and on-disk paths combine these two identifiers with
**different delimiters**, by design:

- **Endpoint names use a flat `<workspaceHash>#<instanceId>` segment.**
  Named pipes (Windows) and Unix sockets (POSIX) live in a flat
  OS-managed namespace; there are no nested pipe paths. The `#`
  separator is a string delimiter that survives the flat namespace
  and lets the launcher derive all four endpoints deterministically
  from the same `(workspaceHash, instanceId)` pair.
- **On-disk paths use a nested `<workspaceHash>\<instanceId>\`
  layout** (POSIX equivalent: `<workspaceHash>/<instanceId>/`).
  Directory enumeration over a workspace's instance history is a
  first-class housekeeping operation, and a hierarchical layout
  lets a sweep walk every subtree belonging to one workspace in
  one `Directory.EnumerateDirectories` call.

| Artefact | Path |
|---|---|
| Endpoint names (four, one per kind, per launcher instance) | `autocontext-engine:rpc@<workspaceHash>#<instanceId>`, `autocontext-engine:events@<workspaceHash>#<instanceId>`, `autocontext-engine:health@<workspaceHash>#<instanceId>`, `autocontext-engine:logs@<workspaceHash>#<instanceId>` |
| Per-instance engine subtree (logs + future engine-owned artefacts) | `%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\` (Windows) / `$XDG_CACHE_HOME/autocontext/<workspaceHash>/<instanceId>/` or `~/.cache/autocontext/<workspaceHash>/<instanceId>/` (POSIX) |
| Engine log files | `…\<workspaceHash>\<instanceId>\logs\engine.log` (rotating, lifetime-of-process) and `…\<workspaceHash>\<instanceId>\logs\crash.log` (write-once tombstone, only on unhandled-exception / fail-fast exit), under the per-instance subtree above |
| Per-worker log files (one per spawned worker; engine-owned, routed by `category` prefix) | `…\<workspaceHash>\<instanceId>\logs\worker-<workerId>.log` |
| Client cache root | `…\<workspaceHash>\<instanceId>\cache\<client>\`, under the same per-instance subtree |
| Shared engine-liveness registry (one file, shared by every live engine on the machine) | `%LOCALAPPDATA%\autocontext\engine-registry.json` (Windows) / `$XDG_CACHE_HOME/autocontext/engine-registry.json` or `~/.cache/autocontext/engine-registry.json` (POSIX) |

A new on-disk artefact must reuse the nested
`<workspaceHash>\<instanceId>` (POSIX: `<workspaceHash>/<instanceId>`)
segments; never invent a parallel identifier and never flatten the
two segments back into a workspace-only path. The same workspace
from different launchers hashes to one workspace identity but
resolves to different engines (different `<instanceId>` in the
endpoint and a different `<instanceId>` subdirectory under the
shared `<workspaceHash>` parent); different workspaces hash to
different identities regardless of launcher. Surface-form
normalisation (case + trailing separators) exists precisely to
collapse the unintentional multi-engine cases that arise from
benign path-shape differences alone — the launcher dimension is
additive on top, and is intentionally not collapsed; symlink /
junction aliasing is intentionally **not** collapsed either, for
the reasons in the *Why surface-form, not resolved* paragraph
above. Per-instance scoping for both logs and client
caches is the price of isolation: two launchers on the same
workspace must not interleave their log lines (a postmortem
reader needs to identify which launcher crashed, not assemble a
merged history) and must not share a cache root (a hook in one
launcher would otherwise be able to read or corrupt files a hook
in the other wrote). The cost is that postmortem and disk-usage
tools must enumerate `<workspaceHash>\<instanceId>` subdirectories
under `…\autocontext\` rather than looking at one flat
workspace-keyed file.

### P5. On-disk path ownership is explicit and exclusive

Every on-disk path AutoContext touches has exactly one owner:

| Path | Owner | Read | Write |
|---|---|---|---|
| `<workspace>/.autocontext.json` | engine | engine | engine |
| `<workspace>/.github/instructions/<name>.instructions.md` | user | engine | user |
| `<host-bundle>/engine/...` (`<vsix>/`, `<plugin-root>/`, GitHub-release tarball) | build | engine reads bundled side-cars at startup | nobody at runtime |
| `%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\logs\engine.log` (POSIX equivalent) | engine | engine, postmortem readers, `Logs.GetEngine` / `Logs.TailEngine` callers | engine |
| `%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\logs\crash.log` (POSIX equivalent) | engine | postmortem readers (humans, peer engines' housekeeping diagnostics) | engine — written once by the dying engine's unhandled-exception / fail-fast handler; never streamed via `Logs.*` (it is a tombstone, not a tail-able feed) |
| `%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\logs\worker-<workerId>.log` (POSIX equivalent) | engine | engine, postmortem readers, `Logs.GetWorker` / `Logs.TailWorker` callers | engine (one file per spawned worker; records arrive via `Engine.WriteLog` and are routed by `category` prefix) |
| `%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\cache\<client>\…` (POSIX equivalent) | the writing client | writing client | writing client |
| `%LOCALAPPDATA%\autocontext\engine-registry.json` (POSIX equivalent) — shared engine-liveness registry | every live engine (co-owned) | every engine on shutdown, every `Engine.RegistryEntries` caller | every engine **appends** its own entry on start and removes its own entry on graceful shutdown; never touches peer entries |

Three rules fall out and the implementation must enforce all three:

- **`Resources/` is read-only at runtime.** No engine-mutation,
  ever. Anything the engine wants to persist goes in
  `.autocontext.json`, the engine log file, or the OS user-cache
  root. Failure modes the rule prevents are listed under the
  [Pitfalls](#pitfalls) entry of the same name.
- **Clients never write under their own install directory.** Install
  dirs (`<extensionPath>`, `${CLAUDE_PLUGIN_ROOT}`) are read-only on
  managed installs and get wiped on host upgrade. All client-owned
  on-disk artefacts live under the OS user-cache root with the
  client's own subdirectory.
- **The engine never reads or cleans the client-cache root *of a live instance*.**
  Lifecycle of every file under a live instance's
  `…\autocontext\<workspaceHash>\<instanceId>\cache\…` is the
  writing client's contract with its host (extension storage,
  Anthropic session lifecycle, …). New client-owned subdirectories
  must be documented in the pitfall list with their owning client so
  cleanup responsibility stays unambiguous. Per-instance scoping
  means the engine never has to reason about cross-launcher
  contention either — each engine sees only the cache subtree under
  its own `<workspaceHash>\<instanceId>`. The single carve-out is
  the engine's own housekeeping sweep (see next rule): when the
  owning instance is verifiably dead and its retention window has
  elapsed, the cache subtree is orphaned by definition and any
  live engine doing its shutdown sweep deletes it together with
  the rest of the per-instance subtree. The engine never touches
  the cache root of a *live* instance — not its own, not a peer's.
- **Per-instance subtree cleanup is the engine's own job, mediated
  by the shared liveness registry.** Every engine, on startup,
  appends its own entry to `…\autocontext\engine-registry.json` —
  one file shared by every live engine on the machine, carrying
  `{ workspaceHash, instanceId, instanceLabel, pid,
  processStartTimeUtc, engineVersion, startedAt, retention }`
  per entry. The append is **additive**: because launchers mint a
  fresh `<instanceId>` for every spawn (P4), the engine never
  has to upsert, deduplicate, or rewrite peer entries on startup.
  On graceful shutdown the engine removes its own entry. A crash
  leaves the entry stale; that is intentional, because staleness
  is exactly the signal the next graceful-shutdown sweep
  consumes. Writes use `FileShare.None` with exponential-backoff
  retry (same discipline as `.autocontext.json`); concurrent
  engine starts serialise on the handle, no engine ever rewrites
  another engine's entry. The engine exposes the file's current
  contents over the wire as `Engine.RegistryEntries` (see the
  RPC surface section) for observability surfaces (external
  ps-style listings, tree-view badges).

  The cleanup itself runs inside every live engine, on the
  single clock defined in `### Housekeeping`: a shutdown sweep
  after own-entry removal. The sweep pid-checks every remaining
  entry (`pid` exists AND `Process.StartTime` ≈
  `processStartTimeUtc` within tolerance, to defeat pid recycling)
  and treats entries that fail the check as dead. Every
  `…\autocontext\<workspaceHash>\<instanceId>\` directory whose
  `<instanceId>` is not in the live set, *and* whose entry's
  `retention` window has elapsed since `startedAt`, is orphaned
  and gets deleted (whole subtree — logs and cache). Retention is
  honoured per-entry — the *dead* engine's declared `--retention`
  controls when its leftovers expire — so a long-retention
  engine's logs survive even if every subsequent engine declares
  a shorter window. Unregistered subtrees (a crash before the entry was
  flushed, or a legacy flat `<workspaceHash>#<instanceId>`
  directory from before the nested layout) fall back to the
  sweeping engine's own `--retention`.

  No external sweeper exists. Every engine spawn pays the
  housekeeping cost on behalf of every dead peer; the design
  refuses to rely on an external subcommand the user has to remember
  to run.

### P6. Subscriptions are first-class; clients never poll or watch

Every observable engine state has a `*.Subscribe` channel with the
same shape (`Config.Subscribe`, `Instructions.Subscribe`,
`Engine.Lifecycle.Subscribe`, `Agent.Events.Subscribe`):

- **Server-streaming**, one channel per topic.
- **Emits a current-state snapshot on subscribe** so a late subscriber
  never has to ask "what's the current value?" separately.
- **Carries a revision counter** wherever cache invalidation
  matters; clients invalidate on counter change without diffing
  payloads. Revisions are per-engine-instance and reset on
  spawn — clients compare them only when the snapshot's
  `instanceId` matches (see the *Revision counter* subsection
  for the full contract).
- **Lossless within a live subscription, rehydrated on reconnect.**
  The pipe transport delivers in-order while the subscription is
  live; clients that disconnect and reconnect rely on the
  snapshot-on-subscribe rule above to catch up. There is no
  persistent event queue or replay log. UX-only event families
  (`Agent.*`) tolerate lost events even on a live subscription;
  content and lifecycle channels do not, because losing them would
  leave the client with no other path to reconstruct the state.

Clients never poll, never watch files, never re-derive state from
disk. New observable state gets a `*.Subscribe` channel; no
client-side watcher is added.

### P7. Two-layer matching: coarse on the producer, fine on the consumer

When a match decision spans engine state **and** host-native
semantics, the engine performs a coarse set-intersection that **must
remain a strict superset** of any client's fine match; the client
uses its host-native matcher unchanged.

- The seed case is `applyTo` × workspace files: engine intersects the
  internally-derived per-file extension set against
  `Workspace.Detect.extensions`; clients run host-native glob
  matchers (`vscode.languages.match`,
  `Microsoft.Extensions.FileSystemGlobbing`, `minimatch`) on the raw
  `applyTo` string.
- Crossing the streams is forbidden in both directions: pushing fine
  matching into the engine drifts from editor semantics; pushing
  coarse matching into the client re-derives the workspace extension
  set N times across N hosts and drifts the same way.
- The engine never tries to re-implement host-native semantics; the
  client never re-derives engine-owned indices. Any future match-like
  capability (content, path-prefix, identifier) follows the same
  split.
- **The coarse layer parses; it does not normalise.** Parsed
  structure is engine-internal (P3); the client receives the raw
  source and matches it with native semantics.

### P8. Async I/O end-to-end; no sync-over-async, no blocking on hot paths

Every transport, every handler, every storage touch on every hot
path is `async`/`await` from the public entry point down to the OS
call. The engine is a long-lived process serving many concurrent
clients over four pipes (plus optional MCP/stdio); a single
`Task.Wait()`, `.Result`, `GetAwaiter().GetResult()`, or otherwise
synchronous read against a pipe or file handle on a request path
deadlocks the dispatcher and starves every other connection. The
rule applies symmetrically to clients (`AutoContext.Client.Core`,
the TS `EngineDaemonManager`, the CLI binary).

- **Pipe I/O is async.** Accept loops use
  `WaitForConnectionAsync`; reads/writes use `ReadAsync` /
  `WriteAsync` against the pipe (or Unix-socket) stream. The
  client side mirrors this on `ConnectAsync` and the per-pipe
  reader/writer loops.
- **Cancellation is propagated, never swallowed.** Every async
  method takes a `CancellationToken` and forwards it down. The
  engine binds one root token to process shutdown (SIGINT,
  SIGTERM, idle-gate fire) and derives per-RPC tokens off it; the
  CLI binds one to `Console.CancelKeyPress` + `AppDomain.ProcessExit`
  and forwards it through `await foreach` for every streaming
  verb. RPC dispatchers wire each request's framing-level
  cancellation signal to the handler's `CancellationToken`
  (`McpTools.Invoke` is the seed case; every `*.Subscribe`
  channel follows the same shape).
- **Streaming RPCs are `IAsyncEnumerable<T>`-shaped.** Every
  `*.Subscribe` / `Tail*` / `Logs.*` channel emits one envelope at
  a time, drained by `await foreach` on the consumer; producers
  never materialise the full sequence in memory and consumers
  never bulk-await it. The cold-start snapshot frame (P6) and any
  follow-on deltas share the same iterator.
- **File I/O on hot paths is async too.** Reading
  `.autocontext.json`, writing it after a toggle, appending to
  `engine.log` / `worker-<workerId>.log`, and rotating any log
  file all run through the async filesystem APIs. The startup
  sweep and shutdown sweep (see `### Housekeeping`) are
  one-shots running off the request path, so they may use
  synchronous filesystem APIs without affecting any in-flight
  RPC — that exemption is narrow and named, not a general
  licence.
- **The one allowed synchronous call is process-bootstrap.** A
  small block before pipe-bind (argv parse, `EngineOptions`
  build, `Resources/` manifest deserialise) runs synchronously
  because there is nothing to interleave it with yet. Once the
  pipes are bound, every code path is async.
- **No sleep-loops, no `Thread.Sleep`.** Retry, backoff, and
  grace periods use `Task.Delay(..., cancellationToken)` so a
  shutdown signal preempts them immediately.

### P9. Concurrent reads, single-writer per resource, snapshot-immutable across reloads

State the engine owns is read concurrently by many clients across
many transports; writes are rare and serialised. The engine never
holds a lock across an RPC, never serialises read traffic behind a
write, and never lets a corpus reload tear a read in flight.

- **Reads are concurrent and lock-free.** `Instructions.List`,
  `Instructions.Get*`, `Config.Get`, `Workspace.*`, `McpTools.List`,
  `Discovery.RouteForPrompt`, and every `*.Subscribe` snapshot
  frame read from an immutable snapshot pointer. No reader takes
  a lock; no reader blocks another reader.
- **Writes are single-writer per resource and atomic.** The
  resources the engine owns at runtime are the **config**
  (`.autocontext.json`) and the **corpus** (in-memory projection).
  Each has one writer:
  - **Config writes** (`Config.ToggleFile`, `Config.ToggleRule`)
    serialise on one in-process writer guarded by an
    async-compatible mutex (e.g. `SemaphoreSlim.WaitAsync`, never
    a `lock` statement — `lock` would forbid the `await`s the
    writer needs, see P8). The writer mutates the on-disk file
    under `FileShare.None` with exponential-backoff retry,
    refreshes the in-memory snapshot pointer with a single
    atomic store, then releases the mutex and fires
    `Config.Subscribe` / re-evaluates `Instructions.*`
    `disabled` flags.
  - **Corpus reloads** (re-parsing override markdown,
    invalidating `InstructionsFullTextSearchService`, recomputing
    projection) run on one in-process reloader that builds the
    next snapshot off the read path, then atomically swaps the
    snapshot pointer and increments the revision counter.
    Readers in flight against the previous snapshot finish
    against it; readers arriving after the swap see the new
    snapshot. There is no half-applied state on either side of
    the swap.
- **Snapshots are immutable.** Every published snapshot
  (config view, corpus projection, content index, MCP-tool
  catalog, `Workspace.Detect` result) is a frozen value: no
  field on a published snapshot is ever mutated in place. The
  revision counter on `Engine.Lifecycle.reloaded` is the only
  invalidation signal clients need (paired with `instanceId`
  for cross-restart dedup — see the *Revision counter*
  subsection).
- **Subscription fan-out is non-blocking and bounded per
  subscriber.** Tightens P6: every `*.Subscribe` channel
  (`Config.Subscribe`, `Instructions.Subscribe`,
  `Engine.Lifecycle.Subscribe`, `Agent.Events.Subscribe`,
  `Logs.Tail*`, the raw `logs` pipe) writes through a
  per-subscriber bounded send buffer. A subscriber that cannot
  drain in time is disconnected with a terminal
  `{ kind: "evicted", reason: "slow-subscriber" }` frame (logs)
  or a clean stream close (every other channel); a slow
  subscriber **never** back-pressures the producer, the snapshot
  swap, or any other subscriber. The principle is universal
  across pipes and across RPC streams. The producer-side
  complement — coalescing redundant fan-outs at the source so
  one logical change emits one envelope — lives under
  [Reload coalescing: debounce and batch](#reload-coalescing-debounce-and-batch).
- **Hot paths never wait on housekeeping.** The shutdown sweep
  runs off the request path (after every pipe closes, bounded by
  a ~1 s deadline). Idle-gate evaluation is a cheap timer tick,
  not a poll across handlers. No request handler ever waits on
  the sweep, the sweep never waits on a request, and there is no
  startup sweep to overlap with first-pipe-accept latency.

### P10. In-process async hooks are single-subscriber; cross-process fan-out is `*.Subscribe`

Two distinct shapes for "notify someone something happened", chosen
by **process boundary × cardinality** — never blurred, never
mixed:

- **Async callback hook** — options-pattern, **single subscriber**,
  in-process. Used on `EngineOptions`, `ClientOptions`, and any
  analogous configuration seam where the embedder wants the
  engine or client library to call back into the embedder's own
  code. Shape:
  - One delegate slot, awaitable, async-shaped from the
    declaration: `Func<TContext, CancellationToken, ValueTask>?`
    (or the `Task`-returning equivalent if `ValueTask` is
    inappropriate locally — pick once per framework, don't mix).
  - A `HasDelegate` check so the producer can skip work
    (building notification payloads, snapshotting state) when
    no hook is registered. The producer **must** consult
    `HasDelegate` before doing any work that exists solely to
    feed the callback.
  - Null-safe invocation: invoking an unset hook is a no-op,
    not an NRE.
  - Cancellation rides the `CancellationToken` argument (P8);
    exceptions propagate to the awaiter, where the producer
    decides whether to fail the surrounding operation or
    isolate the hook failure.
- **Subscription channel** — server-streaming RPC, **many
  subscribers**, cross-process. Used for every observable engine
  state that more than one client needs to learn about. Shape is
  fixed by P6 and P9: `*.Subscribe` returning an `IAsyncEnumerable`
  of envelopes, snapshot-on-subscribe, revision counter,
  per-subscriber bounded buffer with slow-subscriber eviction.

The line is **process boundary × cardinality**. Anything that
crosses a pipe is a subscription. Anything that fires once inside
one process to notify the embedder's own code is an async
callback hook.

- **Classic multicast `event` / `EventHandler` is forbidden in
  framework code.** It is neither async-shaped nor
  `HasDelegate`-shaped, and every place it would be tempting one
  of the two patterns above is the right answer. The ban applies
  to every `AutoContext.Framework.*` project, `AutoContext.Engine.Core`,
  `AutoContext.Client.Core`, `AutoContext.Engine`, the CLI
  binary, every `AutoContext.Worker.*` project, and the TS `EngineDaemonManager`
  (which has no `event` analogue anyway — the rule is recorded
  for symmetry).
- **No in-process listener pools.** A `List<Func<...>>` of
  callbacks inside the engine or client is **not** allowed —
  that is `*.Subscribe`'s job, done correctly cross-process with
  the buffering and eviction rules above. If a single async
  callback hook is not enough, the answer is a subscription
  channel, not a hand-rolled multicast.
- **One signature shape per framework boundary.** Pick the
  delegate signature for the async callback hook (return type,
  whether the `CancellationToken` is a separate parameter or
  carried inside `TContext`) once for the `AutoContext.Framework.*`
  substrate and reuse it everywhere — `EngineOptions`, `ClientOptions`,
  `WorkerOptions`, future options bags. The wrapper type is an
  implementation choice; the signature shape is a design
  invariant.

Validator: every `EngineOptions.OnX` / `ClientOptions.OnX` /
`WorkerOptions.OnX` slot in framework code is a hook wrapper, not
a raw `Func<>` and not an `event`. Every observable engine state
worth notifying *more than one* client about has a `*.Subscribe`
RPC; nothing else.

## Composition contracts

Only two surfaces from the composition layer are part of the design;
everything else is implementation choice that the implementation plan
owns.

- **`IHostApplicationBuilder.AddAutoContextEngine(Action<EngineOptions> configure)`**
  is the engine library's single public entry point. The
  `autocontext-engine` `Program.Main` calls it; tests call it; nothing
  else does. `EngineOptions` exposes the four CLI-surfaced knobs
  (workspace path, idle timeout, MCP-server mode, version display)
  *and* library-only knobs that deliberately don't surface on the
  command line (corpus root override, endpoint override). The CLI
  surface is locked to the four switches enumerated under
  `### Engine options`; everything else is reachable only through
  in-process composition. See that section for the rejection rule
  and the rationale for keeping endpoint override off the binary's
  argv (P4). Any async-callback hooks that future revisions add to
  `EngineOptions` / `ClientOptions` follow P10's single-subscriber
  shape (see `### P10`); they are **not** classic .NET `event`
  slots.
- **`EngineDaemonManager` (TS, `Nodejs.Core/src/engine/`)** is the
  only shared TS class. Plain class, no DI container, constructed
  with `new` and a workspace path. Owns engine-daemon lifecycle
  (find-or-spawn, supervise, tear-down) and exposes the engine's
  RPC surface as typed methods. Speaks the same wire protocol the
  .NET engine serves; that wire protocol is the cross-host seam,
  *not* a class hierarchy.

The extension and the plugin do not share a composer; they share
the engine **binary** (one process per workspace) and the **wire
protocol** (consumed by `EngineDaemonManager` on the TS side).

## Project layout

The engine and the client dialer are two *libraries*, not two
sub-folders of one library, and the binaries that host them are
thin. Three .NET library tiers under `src/` — a substrate built on
the `Framework.Pipes` leaf plus the
`Engine.Protocol` DTO leaf and the `Workers.Core` worker-side
runtime, two `*.Core` libraries that sit on top of it, and one host
project per binary that exists only to call `Main`:

```
   Framework.Pipes (leaf)              Engine.Protocol (leaf — cross-side DTOs)
          ▲                                   ▲
          └──────────────────┬────────────────┘
                             │
         ┌───────────────────┼───────────────────┐
         │                   │                   │
   Workers.Core         Engine.Core         Client.Core
   (refs Pipes+         (refs Pipes+        (refs Pipes+
    Engine.Protocol)     Engine.Protocol)    Engine.Protocol)
         ▲                   ▲                   ▲
         │ (optional)        │                   │
      Worker.*         Engine (binary)     CommandLine (binary)
                       → autocontext-engine[.exe] → autocontext[.exe]
```

One-way rule: `Framework.Pipes` is a leaf that never
depends on `Engine.*`; `Engine.Protocol` is itself an inert DTO leaf.
`Engine.Core` and `Client.Core` reference `Framework.Pipes` +
`Engine.Protocol` directly and
do **not** reference `Workers.Core`. `Worker.*` references
`Workers.Core`, which transitively brings the rest.

- **`AutoContext.Framework.Pipes`**,
  **`AutoContext.Engine.Protocol`**, and **`AutoContext.Workers.Core`**
  are the substrate every AutoContext .NET process
  depends on (see *What `AutoContext.Framework.*` carries over*).
  `Engine.Protocol` is the cross-side-DTO leaf project: the
  protocol-version integer constant `Engine.Hello` exchanges, the
  endpoint builder (`rpc` / `events` / `health` / `logs` ×
  workspace-hash × instance-UUID — P4), and the discriminated-union
  envelopes that appear on *both* sides of every RPC
  (`Instructions.Get` / `McpTools.Invoke` / `Engine.RegistryEntries`
  entry / the `Engine.WriteLog` log-record envelope). Engine,
  dialer, and every worker depend on it; neither
  `Engine.Core` nor `Client.Core` can own these without the other
  depending on it, so they belong with the substrate.
- **`AutoContext.Engine.Core`** is the engine **as a library**.
  Everything under `### Engine-internal services` lives here
  (`ConfigFileService`, `InstructionsManifestService`,
  `InstructionsBodyProjector`,
  `InstructionsFullTextSearchService`, `WorkspaceContextDetector`,
  `WorkerProcessService`), together with the pipe-server bindings for the
  four pipes and the RPC handlers (one per capability — P1). Public
  surface is `IHostApplicationBuilder.AddAutoContextEngine(Action<EngineOptions>)`
  (see *Composition contracts*). References `Framework.Pipes`,
  `Engine.Protocol`; does **not** reference
  `Workers.Core` (it binds the `health` pipe, never dials it,
  and it spawns workers as separate processes rather than hosting
  their handlers in-process).
- **`AutoContext.Client.Core`** is the **`autocontext` CLI as a
  library** — the embeddable home for every type the CLI binary
  uses internally (e.g. `EngineClient` for the typed RPC surface,
  the four-pipe dialer plumbing, the cold-start-or-attach resolver,
  the subscription consumers, `IEngineSpawner`). Public surface:
  `IHostApplicationBuilder.AddAutoContextClient(Action<ClientOptions>)`.
  Consumed by `AutoContext.CommandLine` (the `autocontext[.exe]`
  binary) and by third-party .NET code that wants CLI-shaped
  behaviour in-process without taking a dependency on the CLI
  binary itself (custom integrations, regression harnesses, future
  JetBrains plugins, an `AutoContext.VsCode.Cs` rewrite). It is
  **not** a counterpart of the TS `EngineDaemonManager` —
  `EngineDaemonManager` owns engine-daemon lifecycle on the TS
  host side (find-or-spawn, supervise, tear-down) for the
  extension and hook scripts, which is a different responsibility
  with a different consumer set; the fact that both happen to dial
  the engine's wire protocol does not make them parallel. See
  [autocontext-cli.md](./future/autocontext-cli.md) for the full CLI-as-library
  picture. References `Framework.Pipes`, `Engine.Protocol`;
  same reason as `Engine.Core` for not
  referencing `Workers.Core`.
- **`AutoContext.Engine` (binary)** is the engine host. `Program.Main`
  parses argv per `### Engine options`, calls
  `AddAutoContextEngine(...)`, runs the host. Published per-RID as
  `autocontext-engine[.exe]` (see *Distribution*).
- **`AutoContext.CommandLine` (binary)** is the CLI host. `Program.Main`
  parses subcommands (see [autocontext-cli.md](./future/autocontext-cli.md)), calls
  `AddAutoContextClient(...)`, dispatches verbs. Published per-RID
  as `autocontext[.exe]`.
- **Outside the pipe graph** sit three projects that carry no
  transport dependency. `AutoContext.Instructions.Parser` is a plain
  library — the single instructions-file parser (syntax layer plus
  structured model) compiled into *both* the build-time generator and
  the engine runtime, so one implementation backs manifest generation
  and per-request projection. `AutoContext.Instructions.Manifest.Generator`
  (`instructions-manifest-gen`) and
  `AutoContext.Workers.Manifest.Generator` (`workers-manifest-gen`) are
  build-time console tools that `AutoContext.Engine.csproj` imports as
  `.targets` and sequences via `ReferenceOutputAssembly=false` project
  references; they write into the binary's `Resources/` before compile
  and ship nothing at runtime.

**Neither `*.Core` library references the other.** `AutoContext.Engine.Core`
binds pipes and serves RPCs; `AutoContext.Client.Core` dials
pipes and consumes RPCs. The only thing they share is the
`AutoContext.Framework.*` substrate. Two binaries hosting both libraries in
one process is technically possible (a hypothetical "thick engine
that also dials a peer") and structurally permitted, but no shipped
binary does this today and the layout makes the asymmetry visible
to anyone reading the project graph.

**Sharing-principle caveat.** This split must not become an excuse
to introduce portability abstractions — no `IFileSystem`, no
`IWorkspace`, no engine/client-agnostic "AutoContext core"
interface set the two libraries program against. The engine *binds*
pipes, the client *dials* them; that asymmetry is intentional and
the wire is the only seam (`## Sharing principle`). Library
boundaries enforce direction-of-flow at the project graph level;
they do not invite a third "shared logic" layer between the
`Framework.*` substrate and the two halves.

**Workers are unchanged in role.** A .NET `AutoContext.Worker.*`
project that wants the worker-host scaffold adds a single
`<ProjectReference>` to `AutoContext.Workers.Core`
and picks up the substrate projects transitively
(`Workers.Core` references `Framework.Pipes` +
`Engine.Protocol`). The transitive set
gives it the worker-host scaffold and
`AddEngineLoggerProvider()` from `Workers.Core`, the
transport primitives from `Pipes`, and the wire DTOs from
`Engine.Protocol`. That reference is optional: a worker may instead
speak the dispatch protocol directly over `Framework.Pipes` +
`Engine.Protocol` (as the test-tree `AutoContext.Worker.Test.Driver`
does), or implement it on another runtime entirely (as the Node worker
does).
Workers do not reference
`AutoContext.Engine.Core` (they are spawned *by* it, not hosted
*in* it) and do not reference `AutoContext.Client.Core` (they
speak a narrower wire than full RPC clients do).

**Test-project layout** mirrors every library project one-to-one:

| Test project | Covers |
|---|---|
| `AutoContext.Framework.Pipes.Tests` | Transport primitives — `PipeListener`, codec, keep-alive client, exchange/streaming-client triad |
| `AutoContext.Engine.Protocol.Tests` | DTO envelope round-trips (including the log-record envelope), endpoint builder, source-generated JSON contexts |
| `AutoContext.Workers.Core.Tests` | `WorkerHostBuilderExtensions`, `WorkerTaskDispatcherService`, `WorkerHealthMonitorService`, `CorrelationScope`, and the worker→engine log sender (`EngineLoggerProvider`, `EngineLogIngestRing`, write-log client) |
| `AutoContext.Engine.Core.Tests` | Engine-internal services, RPC handlers, pipe-server bindings; absorbs today's `AutoContext.Mcp.Server.Tests` |
| `AutoContext.Client.Core.Tests` | Typed RPC clients, subscription-stream consumers, dialer back-pressure / reconnect behaviour |
| `AutoContext.Engine.Tests` | Binary host wiring — argv parsing, `AddAutoContextEngine` composition, exit codes |
| `AutoContext.Build.Tasks.Tests` | `BuildInstructionsManifestTask` output fixtures and `ApplyToRoundTripVerifier` invariants (the task itself is also exercised end-to-end by every other project's build) |
| `AutoContext.Worker.*.Tests` | Unchanged — per-worker task suites against the testing harness |

`AutoContext.Mcp.Server.Tests` is retired into
`AutoContext.Engine.Core.Tests` (the MCP server *is* the
engine — see *What the engine absorbs from today's topology*).

**Future subset-library carve-out is a possibility, not v1.**
A consumer that wants only the corpus projection — say, a static
documentation generator that wants `InstructionsFullTextSearchService` and
`InstructionsBodyProjector` without any pipe-server machinery
— could be served by a future `AutoContext.Engine.Core.Instructions`
slice carved out of `AutoContext.Engine.Core`. This is
explicitly **not** a v1 split. Pre-splitting on speculative
embedding scenarios produces more boundaries to maintain than
consumers to serve; the carve-out lands the day a real consumer
asks for it.

## Distribution

The engine must be discoverable from a cold Anthropic plugin
SessionStart hook (no VS Code extension running, no PATH guarantee).
Decision:

- `autocontext-engine` is published per-RID by `dotnet publish -r <rid>
  --self-contained` from `scripts/package.ps1`. No Node runtime is
  bundled; the engine and every subcommand are pure .NET.
- **Supported RIDs:** `win-x64`, `win-arm64`, `linux-x64`,
  `linux-arm64`, `osx-x64`, `osx-arm64`. Resolved at runtime from
  `process.platform` + `process.arch` on the TS side and from the
  bundled binary path on the .NET side. Unsupported combinations
  surface a hard error from the spawner; there is no in-process
  fallback path.
- Per-platform shipped artefact (the **same** layout in every
  target). Build output stages per-RID under
  `artifacts/engine/<rid>/...`; per-platform packaging
  (`vsce package --target <target>` for the VSIX, the equivalent
  per-platform plugin release, one GitHub-release tarball per RID)
  selects the matching `<rid>/` and copies its contents into
  `engine/` in the shipped artefact. The user-visible bundle has
  **no `<rid>/` segment**:

  ```
  engine/
    autocontext-engine[.exe]                       # engine binary (this doc)
    <framework dlls / runtime files>               # self-contained .NET runtime for the engine
    Instructions/                                  # curated corpus (read-only side-car)
      <name>.instructions.md
    Resources/                                     # build-generated + hand-authored read-only manifests
      instructions-catalog.json                    # hand-authored curatorial layer (categories,
                                                   #   label, membership, activationFlags)
      instructions-manifest.json                   # build-generated per-file facts (section maps,
                                                   #   parsed applyTo extension sets,
                                                   #   version, contentHash, hasChangelog)
      mcp-tools-registry.json                      # source-of-truth tool→worker dispatch table (flat tools[])
      mcp-tools-registry.schema.json               # JSON-schema for the registry
      mcp-tools-catalog.json                       # hand-authored UI catalog for McpTools.List
      mcp-tools-catalog.schema.json                # JSON-schema for the catalog
      workers.json                                 # build-generated worker manifest
    Workers/                                       # per-worker subdir, key = workers.json `id`
      workspace/AutoContext.Worker.Workspace[.exe] # self-contained per-RID dotnet worker
        <its runtime files>
      dotnet/AutoContext.Worker.DotNet[.exe]
        <its runtime files>
      web/index.js                                 # node worker (Node runtime is host-supplied)
        <its bundle / node_modules>
  ```

  Three flat side-car directories sit next to the engine binary:
  `Instructions/` (curated corpus), `Resources/` (read-only JSON
  manifests), `Workers/` (per-worker subdirs). Capitalisation
  follows .NET resource-folder convention; JSON filenames are
  kebab-case. The engine resolves each from
  `AppContext.BaseDirectory + "<Dir>"` without any host-supplied
  path — because the shipped layout has no `<rid>/` segment, the
  resolver is a clean one-segment join with no `..` traversal.
  The corpus and manifests are RID-independent in content; they
  appear once per shipped artefact (no duplication across RIDs on
  any user's machine, because each user installs exactly one
  per-platform VSIX / plugin release / tarball).
  **Each worker lives in its own `Workers/<id>/` subdir** so
  per-worker self-contained runtimes
  (`dotnet publish -r <rid> --self-contained`) do not collide with
  each other or with the engine's runtime files at the `engine/`
  root. Other host bundles that need an engine copy nest the
  same `engine/` subtree under their own root; they are not
  part of this layout.
- Bundle locations:
  - `<vsix>/engine/...` for the VS Code extension.
  - `<plugin-root>/engine/...` for the Anthropic plugin.
- Hosts resolve the engine binary by joining the resolved root
  (`extensionPath` for VS Code, `${CLAUDE_PLUGIN_ROOT}` for the
  plugin) with `engine/autocontext-engine[.exe]`. No PATH
  dependency, no current-RID lookup at dial time — the shipped
  artefact already matches the platform it was packaged for.
- A standalone GitHub release publishes one tarball per RID with the
  same flat `engine/` layout for users who want to run
  `autocontext-engine` directly.

### Resource manifests

Everything under `Resources/` is **read-only side-car data — hand-authored
or build-generated, copied or written at build time, parsed by the engine
at startup, never written back by the engine**. The engine projects
per-request against workspace state (disabled rules, overrides) instead of
mutating the manifests.

- **`instructions-catalog.json`** — **hand-authored** curatorial layer,
  tracked in source under `src/AutoContext.Engine/Resources/` (the human
  authors it directly; nothing generates it). Top-level it carries the
  category taxonomy (`name` + `description` — the bucket definitions);
  per entry it carries `label`, category `membership`, and the optional
  `activationFlags` that drive the engine's enable/disable evaluation.
  It exists to give a human editorial control over how files are grouped
  in the UI and which workspace flags auto-enable them — deliberately a
  partial view (the always-attached files `copilot`/`autocontext` are
  exempt; they belong to no category). The build-time generator **reads**
  it to cross-validate against the corpus (every entry resolves to a real
  file; every non-always-attached file has a catalog entry; every membership
  resolves to a declared category) but never rewrites it.
- **`instructions-manifest.json`** — **build-generated** by
  `instructions-manifest-gen` over the curated corpus: each file's
  pre-computed section anchor map, parsed `applyTo` extension set (the
  engine-internal output of the Issue #7 parser), `version`,
  `description`, `contentHash`, and `hasChangelog`. It carries
  **no** body text — `InstructionsFullTextSearchService` builds its index
  lazily over the projected bodies `InstructionsBodyProjector` returns, not
  from any manifest seed — and **no** `categories`/`label`/`activationFlags`
  (those are the catalog's) and **no** workspace-state fields (`disabled`,
  `source`, `overridePath` are resolved per request). At startup the engine **merges** the
  generated manifest with the curated catalog into one immutable
  in-memory snapshot, then projects the `Instructions.List` wire rows and
  the `Instructions.Categories` taxonomy from it per request. The on-disk
  manifest, the engine-internal snapshot, and the wire envelopes are
  three decoupled representations (P3) — none constrains another's shape.
  The generator also reads `mcp-tools-registry.json` as a
  **validation input**: every tool an instruction file names in its
  `## MCP Tool Validation` or `## Workflow MCP Tools Triggers`
  section must be a registered tool, and every parameter it names
  there must belong to a tool named in that section, or the build
  fails with the file and line — the same discipline the
  `[locator#fragment]` reference resolver applies to rule and
  section references. The manifest carries each file's resulting
  `mcpTools` list (the tool names, never the parameters — the
  registry stays the only statement of a tool's contract) so
  `Discovery.RouteForFile` can map a path to the checks it owes.
- **`mcp-tools-catalog.json`** — **hand-authored** activation + UI
  catalog for `McpTools.List`, tracked in source under
  `src/AutoContext.Engine/Resources/`. It is the deliberate complement
  to the registry: where the registry says **what** each tool is and
  how it dispatches, the catalog says **when** each tool activates and
  **where** it lives in the UI. Same curatorial concept as
  `instructions-catalog.json` (a hand-authored layer over a separate
  facts file) but its own shape: a hierarchical category tree
  (`name`, optional `parent`, `description`, optional `workerId` and
  `activationFlags`) plus per-tool entries (`name`, `description`,
  `category`). A tool's **UI placement** is its `category`; its
  **activation** is the `activationFlags` accumulated from that
  category up its ancestry and ANDed (so C# resolves to
  `hasDotNet && hasCSharp`). `workerId` is inherited from the nearest
  ancestor category that defines it, so the catalog's tree mirrors the
  registry's flat `workerId` join without restating it per tool. The
  catalog carries **no** model-facing tool contract — descriptions
  here are human-facing presentation copy, independent of the
  registry's model-facing `description`. The engine merges registry +
  catalog at runtime, and per-request projection applies the per-tool
  `disabled` filter on top of the catalog's activation gating.
  Schema-validated against the sibling `mcp-tools-catalog.schema.json`.
  Not generated — there is **no** build-time `mcp-tools.json`
  projection step (the former `mcp-tools-manifest-gen` projector has
  been removed from the tree).
- **`mcp-tools-registry.json`** — **hand-authored** execution
  registry: it describes **what** each tool is for the model and how
  it dispatches (renamed from today's `mcp-workers-registry.json`).
  Each tool's `description` and `parameters` are the model-facing
  contract surfaced over MCP `tools/list`; its `workerId` is the
  source-of-truth dispatch target the engine uses for
  `McpTools.Invoke` (Issue #8). It holds no activation or UI concerns
  — those live in the catalog. Schema-validated at build time against
  the sibling `mcp-tools-registry.schema.json`; the schema file ships
  alongside the registry so external tooling (CI lint, IDE
  intellisense in `mcp-tools-registry.json` itself) can validate
  without reaching into the source tree.
- **`workers.json`** — build-generated worker manifest. Scans
  `src/AutoContext.Worker.*/` projects, derives `id` by stripping
  the `AutoContext.Worker.` prefix and replacing `.` with `-` and
  lowercasing (`AutoContext.Worker.DotNet` → `dotnet`,
  `AutoContext.Worker.DotNet.Roslyn` → `dotnet-roslyn`), derives
  `type` from the project file (`.csproj` → `dotnet`,
  `package.json` → `node`), and writes the actual published
  `entrypoint` path relative to `engine/`. Id collisions fail
  the build. Shape:

  ```json
  {
    "workers": [
      { "id": "workspace", "type": "dotnet",
        "name": "AutoContext.Worker.Workspace",
        "entrypoint": "Workers/workspace/AutoContext.Worker.Workspace.exe" },
      { "id": "dotnet", "type": "dotnet",
        "name": "AutoContext.Worker.DotNet",
        "entrypoint": "Workers/dotnet/AutoContext.Worker.DotNet.exe" },
      { "id": "web", "type": "node",
        "name": "AutoContext.Worker.Web",
        "entrypoint": "Workers/web/index.js" }
    ]
  }
  ```

  The `entrypoint` is written by the build (not derived by the
  engine via convention) so a worker can change its assembly name
  or entrypoint file without touching the engine's resolver.

Source-side locations for the editable inputs the build consumes:

- `src/AutoContext.Engine/Instructions/` — editable curated corpus.
- `src/AutoContext.Engine/Resources/mcp-tools-registry.json` (+
  `mcp-tools-registry.schema.json`) — hand-edited registry and its
  schema. The build copies them as-is into the per-RID staging
  `Resources/` dir.
- `src/AutoContext.Engine/Resources/mcp-tools-catalog.json` (+
  `mcp-tools-catalog.schema.json`) — **hand-authored and tracked in
  source**; the curatorial UI catalog (category tree + per-tool
  entries) and its schema. The build copies them as-is into the
  per-RID staging `Resources/` dir; the catalog is never generated.
- `src/AutoContext.Engine/Resources/instructions-catalog.json` is
  **hand-authored and tracked in source** — it is the curatorial
  layer (categories, `label`, membership, `activationFlags`). The
  build copies it as-is into the per-RID staging `Resources/` dir;
  it is never generated.
- `Resources/instructions-manifest.json` and `Resources/workers.json`
  have **no source-side copy** — they are pure build outputs,
  regenerated every package run. `instructions-manifest.json` is
  written by `instructions-manifest-gen` over the corpus + catalog.

## Pitfalls

- **Engine termination signal.** `autocontext-engine` is launched
  detached, with no inherited stdio handles — every spawner
  (the VS Code extension and Anthropic plugin via Node
  `child_process.spawn(..., { stdio: 'ignore', detached: true })`,
  any .NET spawner via `Process.Start` with
  `UseShellExecute = false` and redirected/null stdio) deliberately
  cuts the engine off from a controlling console so it can outlive
  the spawner. Consequence: `Console.CancelKeyPress` does not
  fire inside the engine. Production termination is
  `--idle-timeout` plus the OS-level signal path
  (`AppDomain.ProcessExit` for SIGTERM / Windows stop). Foreground
  invocations (smoke tests, `dotnet run`) reach the SIGINT path
  normally because they keep the console attached.
- **MCP-server role argv discipline.** The implementation trap:
  the daemon role and the MCP-server-only role share one binary
  but their argv contracts are disjoint, and the dispatch happens
  on a single switch (`--mcp-server`). The trap is conditionally
  registering `AddMcpServer().WithStdioServerTransport()` *and*
  the pipe-server / housekeeping hosted services in the same
  DI graph based on `--mcp-server` presence — that path keeps
  enough shared state alive for the two roles to drift toward
  the old single-process facade design as the codebase evolves.
  Implementation rule: parse argv first, then branch into one of
  two disjoint `IHostBuilder` compositions — the MCP-only branch
  registers `AddMcpServer().WithStdioServerTransport()` and
  nothing else state-bearing (no pipe servers, no
  `FileSystemWatcher`, no `engine-registry.json` writer, no
  worker dispatcher), the daemon branch registers everything
  *except* MCP. An unconditional `WithStdioServerTransport()` in
  the daemon branch would also hit immediate EOF on the
  `stdio: 'ignore'` stdin every non-MCP spawner passes, so the
  branch boundary doubles as the EOF-suicide guard. See
  [Engine binary](#engine-binary) for the role split and
  [Lifecycle](#lifecycle) > *MCP-server-only role is out of scope*
  for the daemon-side disclaimer.
- **`autocontext-engine --version` is RID-independent.** Driven by
  `AssemblyInformationalVersionAttribute` set from `version.json`;
  do not bake the RID into the version string. The corpus and the
  version are RID-independent in content.
- **Engine-owned on-disk artefacts.** The engine writes its
  on-disk artefacts in two places. The per-instance subtree
  `%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\`
  (Windows; equivalents under the OS user-cache root on POSIX)
  holds the engine-written log files under the `logs\`
  subdirectory: `engine.log` for engine-emitted records (the
  rotating, lifetime-of-process feed), `crash.log` for the
  write-once tombstone the dying engine emits from its
  unhandled-exception / fail-fast handler (absent on graceful
  shutdown; see the *Don't crash the crash writer* pitfall),
  and one `worker-<workerId>.log` per spawned worker
  receiving worker-emitted records that arrive via `Engine.WriteLog`
  and are routed by `category` prefix (see the *Log pipeline
  backpressure* and *Worker–engine connectivity* pitfalls). Every
  file under `logs\` is engine-owned per P5 — the engine is the
  sole writer — and is rotated in-process by the `--log-rotation`
  thresholds (see `### Housekeeping` > *Log rotation*), with
  rotated history pruned per `--retention`. Active files survive
  shutdown for postmortem reading by anyone who knows the
  launcher's UUID; rotated files survive until retention elapses.
  No engine-owned cache
  directory exists — every engine cache is in-memory and invalidates
  on internal events. The sibling `cache\` subdirectory under the
  same per-instance root is **client-owned**: the writing client is
  responsible for its lifecycle, and the engine neither reads nor
  cleans those paths while the instance is live. Outside the per-instance subtree,
  one shared file at the autocontext root —
  `%LOCALAPPDATA%\autocontext\engine-registry.json` (POSIX
  equivalent) — is co-owned by every live engine on the machine:
  each engine **appends** its own entry on start (fresh
  `<instanceId>` every spawn, no upsert) and removes its own entry
  on graceful shutdown, never touching peer entries. A crash leaves
  the entry stale on purpose, because that is the signal the next
  engine's graceful-shutdown sweep uses to identify orphaned
  instances (see P5 and `### Housekeeping`).
  Clients must never cache under their own install directory
  (`<extensionPath>`, `${CLAUDE_PLUGIN_ROOT}`) — those are
  read-only on managed installs and get wiped on host upgrade.
  Document any new client-owned subdirectory in this list with
  its owning client so cleanup responsibility stays unambiguous.
  Per-instance subtree sweeping for orphaned
  `…\autocontext\<workspaceHash>\<instanceId>\` directories is the
  engine's own shutdown-only housekeeping job, mediated by the
  shared registry (see P5).
- **`engine-registry.json` entry lifecycle: append-on-start,
  remove-on-graceful-shutdown, leave-stale-on-crash.** Every engine
  appends its own entry to the shared registry as part of startup
  (after pipe bind, before accepting connections). Because the
  launcher mints a fresh `<instanceId>` for every spawn (P4 —
  launchers never reuse a UUID across respawns), the append is
  unconditionally additive: there is no upsert, no
  same-`instanceId` overwrite, no deduplication step. The engine
  removes its own entry from the `AppDomain.ProcessExit` /
  SIGTERM / Windows service-stop path on the way out. A crash,
  kill -9, or power loss leaves the entry in place; this is
  **intentional**, because the staleness signal is exactly what
  the next graceful-shutdown sweep consumes to identify orphaned
  per-instance subtrees. Two pitfalls follow.
  First, pid recycling: an entry's `pid` field on its own is not
  enough to assert liveness, because the OS may have recycled the
  pid to a different process by the time the registry is read. The
  entry carries `processStartTimeUtc` alongside `pid`, and any consumer
  asserting liveness (including the engine itself when answering
  `Engine.RegistryEntries` for diagnostic callers, and especially
  the housekeeping sweep when deciding what to delete) must compare
  `Process.GetProcessById(pid).StartTime` against
  `processStartTimeUtc` with a small tolerance (~1 s for clock
  jitter); mismatch means the pid was recycled and the entry is
  stale. Second, registry write contention: two engines starting
  concurrently both want to append their entry. Writes use
  `FileShare.None` plus exponential-backoff retry (same discipline
  the engine already uses for `.autocontext.json`), so the OS
  serialises the appends and neither engine corrupts the file. A
  corrupt-file recovery path exists for the case where a write was
  interrupted mid-flush: any engine encountering an unparseable
  registry on startup truncates it and writes only its own entry,
  on the theory that one re-derivable file is cheaper than
  blocking startup forever. The next graceful-shutdown sweep
  encountering the same corrupt file treats every per-instance
  subtree as orphaned (because the registry can no longer attest
  to any liveness) and proceeds against retention as usual; the
  next engine start re-seeds the file.
- **Log pipeline backpressure: workers never block, slow subscribers
  never starve.** The unified-logging design routes every worker
  `ILogger<T>` record through `Engine.WriteLog` into the engine's
  single sink, then fans it out on the `logs` pipe. Two
  failure-modes must not couple. (a) Worker → engine ingest is
  **fire-and-forget with a bounded in-memory buffer; drop oldest on
  overflow**. The worker's `Engine.WriteLog` notification never
  awaits an ack and never throws back into the caller's `ILogger<T>`
  call site — a busy worker that out-runs the engine's ingest rate
  loses its oldest queued records, and the engine emits a single
  `category: "engine.logging"`, `level: "warning"` record
  (`"dropped N worker log records"`) the next time the queue drains,
  so the gap is visible without flooding the log. (b) Engine →
  `logs`-pipe fan-out is **non-blocking per subscriber**: each
  subscriber has its own bounded send buffer, and a subscriber that
  cannot keep up is disconnected (its pipe handle closed) rather
  than allowed to back-pressure the file sink, the engine's
  ingest loop, or any other subscriber. The `logs` pipe is
  passive-observer (P4) precisely because losing a subscriber must
  never affect engine progress. Both rules are non-negotiable:
  inverting either one re-introduces the every-component-has-its-own-log
  problem the unified sink is solving.
- **Worker–engine connectivity: bounded in-memory buffering, no
  on-disk worker spool.** The worker side of `Engine.WriteLog` is
  fire-and-forget with a bounded in-memory ring, not a durable
  spool. The worker's `AddEngineLoggerProvider()` registers an
  `ILogger<T>` provider that — on reachable engine — serialises
  the record, frames it, writes it to the pipe, and forgets it;
  on unreachable engine (engine mid-shutdown, RPC pipe unbound
  during a startup race, worker still inside cold-start) it
  enqueues the record into a bounded ring (default 1000 records or
  1 MiB, whichever fills first; drop-oldest on overflow), retries
  the pipe at exponential backoff, and drains the ring on the
  next successful write. On worker process exit whatever is still
  in the ring is **lost** — there is no on-disk worker-owned spool
  and no replay file the next engine pickup drains. This is the
  deliberate trade-off: `Engine.WriteLog` records are dev-tool
  diagnostics, not flight-recorder data, and engine-only ownership
  of `…\logs\` is worth the loss-on-disconnect-then-crash window.
  On overflow / drop the worker writes one line to **stderr** per
  drop batch (`"engine log dropped N records"`); the engine
  supervises every worker it spawns via `Process.Start` and folds
  captured stderr into `worker-<workerId>.log` under category
  `worker.<workerId>.engine.stderr`, so the *failure to log* is
  itself observable through the engine's own log even when the
  channel that would normally surface it is the broken one. The
  ring bound is intentionally small. A worker that genuinely
  cannot reach its engine for long enough to fill 1000 records is
  in a degraded state the design does not try to make invisible;
  the dropped-record stderr line plus a future health signal
  ("worker spawned but no records received in N seconds") are the
  right surfaces for that condition, not a growing on-disk spool.
- **Cross-instance `.autocontext.json` writes race on disk, not
  on the wire.** Two launchers on the same workspace run two
  independent engines (different `<instanceId>`) with independent
  in-memory state. Both treat `.autocontext.json` as the authority
  of record across instances: writes must take a `FileShare.None`
  handle with exponential-backoff retry, and reads pick up peer
  changes through the engine's existing `FileSystemWatcher` and
  surface as a regular `Config.Subscribe` event. Engines never
  RPC each other — cross-instance change propagation is
  filesystem-mediated only. The visible consequence is a small
  divergence window: an instruction toggle in one VS Code window
  reaches a second VS Code window on the same workspace only
  after the file write completes and the watcher debounce fires.
  Within a single launcher instance there is no divergence — the
  extension and its hooks share the same engine.
- **Hook scripts outside a known launcher need explicit
  `--instance-id` propagation.** Hooks running under the same
  host process that *spawned* a launcher (VS Code Copilot hooks
  inside the AutoContext-extension's VS Code window, Claude Code
  hooks inside that Claude session) inherit the launcher's
  instance UUID through whatever side channel the host already
  uses to pass per-session state — typically an environment
  variable the launcher sets on its own process before spawning
  any child. Hooks running under a host the AutoContext launcher
  cannot reach (a stand-alone agent invocation, a future host
  that does not run alongside one of our clients) have no
  launcher to inherit from and must spawn their own engine with
  a hook-minted UUID. The trap is the in-between case — a hook
  script that *could* reach an existing launcher's engine but
  doesn't, and silently spins up a parallel engine on the same
  workspace, fragmenting `Agent.*` broadcast and burning an
  extra idle-timeout countdown. Hook templates surface the
  propagated UUID prominently; the engine itself does nothing to
  detect this case.
- **Override survival across upgrades.** A workspace-local
  `<workspace>/.github/instructions/<name>.instructions.md` keeps
  winning silently when the bundled source updates in a release.
  The corpus service emits a warning event when override mtime is
  older than bundled mtime; UIs surface it as a non-fatal hint.
- **`Resources/` is read-only at runtime.** (Instance of **P5**.) The engine reads every
  side-car JSON manifest under `engine/Resources/` at startup
  and projects per-request against workspace state
  (`.autocontext.json`, override files, revision counter). It
  **never writes back** to any file under `Resources/` — not to
  patch a disabled flag, not to record a revision bump, not to
  cache anything. Two failure modes this rule prevents: (a) managed
  installs (VSIX, Anthropic plugin) mount their install dir
  read-only or wipe it on upgrade, so a write would either fail at
  runtime or silently disappear; (b) when multiple launchers on
  the same workspace each spawn their own engine, every engine
  reads the same install-time-immutable side-cars, so any
  per-workspace mutation routed through `Resources/` would have
  to be re-applied by every peer to stay consistent — the
  consistency problem the engine consolidation eliminated would
  reappear at the file-system layer. State the engine wants to
  persist goes in workspace state
  (`.autocontext.json`), engine log files under the per-instance
  subtree
  (`%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\logs\engine.log`),
  or the OS user-cache dir — never in `Resources/`.
- **`alwaysAttached` is explicit, not derived.** The set returned
  by `Instructions.GetAlwaysAttached` is the files the catalog's
  `alwaysAttached[]` array declares, not the files
  whose frontmatter omits `applyTo`. Today's corpus has six files
  with no `applyTo` (`copilot`, `autocontext`, `code-review`,
  `design-principles`, `git-commit`, `rest-api-design`); only
  the first two are always-attached. The other four are
  domain-conditional and surface via `Discovery.RouteForPrompt`.
  Engine implementations must not collapse "no `applyTo`" into
  "always-attached" — that would flood every turn with editorial
  guidance the user did not ask for. Override files inherit the
  flag from the bundled source they shadow; an override cannot
  promote or demote a file in or out of the always-attached set.
- **`Instructions.Get` distinguishes `disabled` from `not-found`.** (Instance of **P2**.)
  Both outcomes return no body, but they mean different things:
  `not-found` says the name was never in the corpus (typo, stale
  reference, removed file); `disabled` says the file exists and
  the user has actively muted it. LM-facing surfaces — Copilot's
  `get_autocontext_instructions_file`, future MCP equivalents,
  any tool with a model in the loop — must surface the disabled
  envelope verbatim so the model can tell the user "this rule
  exists but is muted" instead of silently ignoring the request.
  Consumption-mode surfaces with no UI for the user (sub-agent
  materialisation, `additionalContext` injection,
  `GetAlwaysAttached`) treat both outcomes as omission. A client
  that flattens `disabled` to `not-found` (or vice versa) breaks
  the user's mental model of what their `.autocontext.json`
  toggles actually do.
- **LM-tool and MCP-tool handlers are marshalling shims, not
  business logic.** (Instance of **P1**.) The instruction-discovery LM tools registered by
  the VS Code extension and their `instructions_*` MCP-tool
  counterparts on the engine's MCP-server-only role are paper-thin:
  they deserialise input, call the corresponding service method in
  `Engine.Core`, and serialise the result. Trigram indexing, override resolution,
  metadata predicate evaluation, coarse `applyTo` filtering (see
  the next pitfall for the coarse/fine split), and disabled filtering
  all live inside the engine's service layer; both surfaces share
  one implementation. The failure mode this rule
  prevents is cross-surface drift — a field added to the MCP-tool
  schema but not to the LM-tool shim (or the reverse), or a filter
  applied in one transport but not the other, re-introducing the
  two-surfaces-could-diverge problem the engine consolidation
  eliminated. If a field can't be expressed on the engine RPC, it
  can't be expressed on either tool surface either.
- **`McpTools.Invoke` and MCP `tools/call` share one handler, one
  validator, one worker dispatcher.** (Instance of **P1**.) The pipe-RPC `Invoke` method
  is a marshalling shim parallel to the instruction-discovery shims
  above — input deserialisation and result serialisation only.
  `inputSchema` validation, override of the worker-pipe dispatcher,
  cancellation forwarding, and `disabled` / `not-found` filtering
  all live in the engine's service layer; the MCP/stdio `tools/call`
  handler is the same shim with a different transport. Resist the
  temptation to give the pipe surface a "richer" envelope than MCP
  — extra structured error fields the MCP spec doesn't carry,
  pipe-only progress channels, pipe-only timeout knobs — because
  every such asymmetry re-opens the two-surfaces-could-diverge
  problem. The discriminated `InvokeResponse` already encodes every
  distinction the pipe needs (`ok` / `tool-error` / `schema-error` /
  `disabled` / `not-found`), and `content: ContentBlock[]` matches
  MCP's `CallToolResult.content` block-for-block so a recorded pipe
  response can be diffed against a recorded stdio response and
  produce zero output.
- **`applyTo` matching: engine does coarse, client does fine. Never
  cross the streams.** (Instance of **P7**.) The engine's coarse layer is a set
  intersection of extension sets (the engine's internally-derived
  per-row extension set ∩ `Workspace.Detect.extensions`); the
  client's fine layer is glob
  × glob matching via its host's native matcher
  (`vscode.languages.match` in VS Code, `Microsoft.Extensions.FileSystemGlobbing`
  in the CLI, `minimatch` in hooks). Pushing fine matching into the
  engine introduces silent drift from editor semantics — a
  hand-rolled .NET glob library will not be byte-identical to
  `vscode.languages.match` for `**` greediness, case sensitivity,
  `.gitignore`-excluded paths, or brace expansion edge cases.
  Pushing coarse matching into the client re-derives the workspace
  extension set N times across N hosts and drifts the same way.
  The coarse layer must remain a strict superset of any client's
  fine result — if the engine excludes a row the client would have
  matched, the entire surface goes inconsistent.
- **The `applyTo` parser parses, it does not normalise, and its
  output is engine-internal.** (Instance of **P3**.) The engine-side `applyTo` parser
  (inside `instructions-manifest-gen`, runs at build time and feeds
  the same parsed extension set into
  `Resources/instructions-manifest.json`) splits comma-separated
  lists,
  trims whitespace, brace-expands `{a,b,c}` groups, and extracts an
  extension set. The result is consumed by the engine's coarse
  `applyTo` filter and by `Discovery.RouteForPrompt`'s extension
  index; it is **not** emitted on the `Instructions.List` wire
  envelope. Clients receive only the raw `applyTo` string and hand
  it to their host-native fine matcher unchanged. The parser must
  **not** attempt to canonicalise globs, simplify `**` patterns,
  deduplicate semantically-equivalent globs, or otherwise reason
  about what a glob means — that is matching, and matching is the
  client's job. An internal round-trip check (recomposed glob list
  equals the source `applyTo` modulo whitespace) must hold for
  every entry in the corpus; if a parser change ever breaks the
  round-trip, the parser is doing too much. Resist the temptation
  to publish the parsed structure as a convenience field on the
  wire — every client that ever wanted it could trivially derive
  it from the raw string, and publishing it locks in details the
  engine should be free to change.
- **Engine bootstrap is the chicken-and-egg.** The Anthropic plugin
  SessionStart hook runs before any extension. The engine must be
  self-spawning from a cold hook invocation — do not design a flow
  that requires the VS Code extension to start it first.
- **Endpoint collisions across UNC / case-variant paths.**
  Normalise the workspace path (uppercase on Windows, trim
  trailing separators; **no** symlink resolution — see § P4 for
  the rationale) before hashing for the endpoint; otherwise two
  hosts on "the same" workspace get different engines. Two hosts
  on two surface-distinct paths that happen to alias the same
  directory via a symlink / junction get two engines by design;
  the user resolves it by picking one canonical surface path.
- **Concurrent first-connect.** Two hosts racing to spawn the
  engine will both spawn one — each mints its own per-launch
  UUID, so the endpoints are distinct and both engines start
  independently by design (this is two engines, not a race).
  A second engine starting under the *same* `<instanceId>` is a
  launcher bug under the per-launch-UUID contract (P4); the
  engine fails loudly on pipe-bind collision with a non-zero
  exit, not silently as an "idempotent bind".
- **Don't crash the crash writer.** `crash.log` is the engine's
  end-of-life tombstone — the one diagnostic that survives when
  the regular logging pipeline has already given up. It is
  written by an `AppDomain.UnhandledException` /
  `TaskScheduler.UnobservedTaskException` / top-level
  `Program.Main` `try`/`catch` handler, **and** by the fail-fast
  paths that abort startup after argv parse but before pipe-bind
  (notably the same-`<instanceId>` pipe-bind collision in
  `InstanceIdCollisionWatchdog` and any post-argv manifest /
  resource load failure). The writer needs both `<workspaceHash>`
  and `<instanceId>` to construct its target path, so failures
  that abort *before* argv has been parsed enough to recover
  those two values (malformed argv, `--help` mis-invocation,
  missing required flags) cannot produce a `crash.log` — those
  exit with a stderr diagnostic and a non-zero code, and are out
  of scope for the tombstone surface.
  The writer is deliberately minimal: synchronous
  `File.WriteAllText` against a single per-instance path, no DI,
  no `ILogger`, no async, no buffered channels, no allocations
  beyond the JSON serialisation buffer, and a hard `try`/swallow
  around the write itself — a failed `crash.log` write must never
  mask the original fault. The handler then `Environment.Exit`s
  with a non-zero code (or rethrows, depending on entry-point
  shape). Graceful shutdown (`Engine.Shutdown` RPC, parent-pid
  watchdog, idle-timeout watchdog) does **not** invoke the crash
  writer and does **not** produce a `crash.log` — those are
  expected exits, not faults. External kills (SIGKILL, BSOD,
  power loss) inherently cannot produce a `crash.log` either; a
  peer engine's shutdown-sweep housekeeping is the only signal
  in that case. `crash.log` lives inside the per-instance subtree
  and is reaped along with everything else under that subtree
  once `--retention` elapses; it is never streamed via `Logs.*`
  because it is a tombstone, not a tail-able feed.
- **Corpus drift against the registry.** An instruction file's
  `## MCP Tool Validation` section is prose the model acts on: it
  names a tool and the parameters to pass. The registry is the only
  place a tool's name and parameters are defined, and nothing tied
  the two together at authoring time — a tool renamed in the
  registry leaves every section that cited the old name reading
  naturally and failing silently at the model's end, where "the tool
  does not exist" degrades into "skip the check". This happened: the
  engine corpus was copied from the extension's, whose sections named
  the legacy server's `analyze_csharp_code` and `originalPath`, and
  the engine registry then renamed them to `analyze_csharp_code_style`
  and `filePath` without the copy following. Each corpus is bound to
  the registry of the server that serves it; a copied corpus inherits
  the old binding silently. `instructions-manifest-gen` therefore validates every
  tool and parameter token in those sections against the registry
  and fails the build on a mismatch. Do not loosen the shape rules
  to let a "nearly right" name through; the model will not
  fuzzy-match either.
- **Corpus drift between RIDs.** The corpus is duplicated per RID
  in the packaged artefact. The build must copy from one source
  (`src/AutoContext.Engine/instructions/`) into every RID staging
  dir; no per-RID corpus edits are permitted. Validator asserts
  byte-equality across RIDs in a build.
- **Do NOT** port the engine to TypeScript. The engine is .NET; the
  TS side ships only `EngineDaemonManager` and the existing pipe
  transport.
- **Do NOT** invent cross-host portability seams. Using
  `Microsoft.Extensions.Hosting` (`IHostEnvironment`, `ILogger<T>`,
  `IOptions<T>`, `IConfiguration`) inside the engine is expected
  and matches the rest of the .NET solution. What we don't do is
  invent a custom `IFileSystem` / `IWorkspace`-style interface that
  pretends the C# engine and the TS extension share code — they
  share a wire protocol, not a class hierarchy. The TS-side
  `EngineDaemonManager` stays a plain class, no DI container.
- **Do NOT** fold workers into the engine. Workers are transient
  task executors with their own crash / lifecycle profile. The
  engine spawns them via the same lazy `ensureRunning(workerId)`
  gate `WorkerControlClient` uses today; workers stay as separate
  binaries (`AutoContext.Worker.DotNet`,
  `AutoContext.Worker.Workspace`, `AutoContext.Worker.Web`).
- **Do NOT** invent a launcher-side URI scheme for spawning the
  engine or workers. The engine binary is launched directly (by an
  MCP host or by any other spawner). Workers are launched by the
  engine. No host-side `service://` indirection is part of the
  engine's contract.

## Implementation phase shape

The design doc records only the *shape* of the rollout below.

Shape:

- **Skeleton.** `AutoContext.Engine` project, empty
  `AddAutoContextEngine`, `autocontext-engine --version`, sibling
  `AutoContext.CommandLine` skeleton.
- **Engine library populated.** Config store, corpus reader,
  projector, corpus service, workspace detection, pipe-listener /
  idle-watchdog hosted services, RPC handlers, MCP-tool catalog,
  worker dispatch, MCP-server-only role composition.
  `EngineClient` (.NET, inside `Client.Core` — the CLI-as-library)
  and the TS-side `EngineDaemonManager` (engine-daemon lifecycle
  for the extension and hooks) land in this skeleton step too;
  they share only the wire protocol.
- **MCP server retirement.** `AutoContext.Mcp.Server`'s
  `Program.Main` shrinks to delegating into `AddAutoContextEngine`,
  then is deleted entirely once nothing references it. The MCP host
  servers manifest is repointed at `autocontext-engine`.
- **Extension migration.** The four sideband pipe servers
  (`LogServer`, `HealthMonitorServer`, `WorkerControlServer`,
  `AutoContextConfigServer`) are deleted from the extension. The
  in-extension projection / config / corpus classes are deleted in
  the same release that ships the engine. The extension becomes a
  pure `EngineDaemonManager` consumer plus VS Code-specific UI.
- **Anthropic plugin re-pointing.** SessionStart and any other
  hooks call `EngineDaemonManager` against the engine pipe. Hooks
  surface `Engine.Hello` failure as a structured hook error;
  there is no in-hook disk-read fallback (engine and plugin
  ship versioned together inside the plugin root).
- **Post-migration surface.** With the engine the only reader, the
  work turns to what the agent experiences: the corpus's tool
  obligations are validated against the registry at build time; the
  `PostToolUse` / `Stop` hooks discharge those obligations
  automatically; checks read the project before judging it; a
  workspace can author its own instruction files; the plugin ships
  for hosts without VS Code; and the engine reports what each
  session actually used.

## Companion documents

- [autocontext-cli.md](./future/autocontext-cli.md) — one pipe-RPC client of
  the engine, documented separately.
