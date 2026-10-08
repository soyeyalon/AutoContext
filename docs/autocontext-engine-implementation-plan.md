# Implementation Plan: `autocontext-engine`

> **Companion to** [`future/autocontext-engine.md`](./autocontext-engine.md).
> That document is the design authority; this document is the rollout
> sequence. When the two disagree, the design wins — open a delta in
> the design first, then update this plan.

## Goals and ground rules

- **Preview release, coordinated cutover.** AutoContext is in preview;
  the whole engine lands in this release. There is no parallel
  transitional release that ships both topologies.
- **One source of truth.** Every phase below cites the design-doc
  section it implements (`design §…`) and the current-codebase files
  it replaces or absorbs (`code …`). Reviewers walk the diff against
  both anchors.
- **Codebase conventions, no drift.** Each phase follows existing
  patterns — `.editorconfig`, `build.ps1` (never bare `dotnet`/`npx`),
  naming and structure mirrored from sibling projects, the
  `Worker.*` shape for new worker projects and the `Framework.Tests.Support`
  shape for shared .NET test-support code, the
  `Nodejs.Core` shape for new TS code.
- **Just-in-time scaffolding.** Introduce a new project, folder,
  file, type, or member only in the phase that *uses* it. No empty
  placeholder projects, no empty class libraries with only a no-op
  extension method, no `Instructions/` / `Resources/` folders
  scaffolded for a later phase to fill. If Phase N+k is the first
  consumer of artefact X, Phase N+k is the one that creates X (and
  its sibling test project). Bare-graph scaffolding ahead of time
  hides intent, leaves dead files in the tree, and produces commits
  that don't earn their own behavioural test. Restructuring code
  that already exists (renames, project splits, file moves) is not
  scaffolding-ahead — it's reorganisation of live code and is
  governed by the *Codebase conventions, no drift* rule above.
- **API hygiene by default.** Across every ctor, method, and
  function signature the engine introduces:
  - **Parameter order is intentional, not incidental.** Arrange
    arguments by the role they play for the call site — required
    before optional, identity / context first, the primary subject
    next, collaborators after, behaviour-shaping flags or
    `CancellationToken` last — and stay consistent with sibling
    APIs on the same type. Drive-by reordering across the
    codebase is not allowed; the order chosen when a member is
    introduced is the contract. `CancellationToken` parameters are
    always named `cancellationToken` in full — never `ct`,
    `cancelToken`, or `token` — so call sites read uniformly
    across every project and signatures stay grep-friendly.
  - **Validate required ctor and method parameters at the boundary.**
    Every required reference parameter is null-checked (and
    range/format-checked where meaningful) before the body
    executes — `ArgumentNullException.ThrowIfNull` (C#) or an
    explicit guard / parsed type (TS). No "defensive deep inside"
    re-validation; one guard at the public surface.
  - **Optional parameters mean genuinely optional.** A parameter
    is `optional` / has a default only when omitting it produces
    well-defined, useful behaviour distinct from passing the
    default explicitly. "Convenience" defaults that paper over
    missing information do not ship; require the caller to be
    explicit and let the type system carry the intent.
  - **Names are readable, not short.** Types, members, locals, and
    parameters are named for the role they play in their context
    — `instructionsManifestSnapshot` over `corpus`, `pendingReload`
    over `pr`, `workspaceHash` over `wh`. Favour the longer name
    that reads correctly out loud and that a reviewer can reason
    about without opening the call site. Cryptic abbreviations,
    one- or two-letter identifiers, and Hungarian prefixes are
    rejected at review; well-established domain terms
    (`rpc`, `mcp`, `uri`, `json`) stay as-is.
  - **Member order follows StyleCop.** Within every C# type,
    members appear in StyleCop SA1201 order — fields,
    constructors, finalizer, delegates, events, enums, interfaces,
    properties, indexers, methods (including operators and
    conversion operators), then nested types — and within each
    group access goes `public` → `internal` → `protected internal`
    → `protected` → `private protected` → `private` (SA1202),
    with `static` before instance (SA1204). New members slot into
    the correct group rather than being appended; reorder in a
    separate, clearly-scoped commit when an existing file drifts.
- **Logging is a feature, not a courtesy.** Every phase treats
  structured logging as a first-class deliverable of the code it
  introduces, not as a debug-only afterthought. Engine, worker, and
  client code log lifecycle transitions, every RPC handled and
  every envelope kind returned, every snapshot swap and revision
  bump, every subscriber attach / drop, and every error path — at
  appropriate levels, through the unified logging pipeline
  (`design § Engine logging` and the TS / .NET logger sharing
  contract). A phase that adds behaviour without adding the
  corresponding log surface is incomplete.
- **Tests every phase, not "a test phase".** Every phase ships with
  unit tests against the new code. Phases that cross a process
  boundary also ship integration tests (engine spawned via
  `Process.Start` or `child_process.spawn`). No phase merges with red
  tests; no phase defers its tests to a later phase.
- **Each phase compiles and tests green at its boundary** so a
  reviewer can cherry-pick the branch at any phase boundary and the
  full `.\scripts\prepare.ps1` is green.
- **Re-read the design before every phase.** Before opening a phase
  branch, re-read the sections of
  [`future/autocontext-engine.md`](./autocontext-engine.md)
  that the phase cites (the `design §…` anchors in its header, plus
  any subsections they cross-reference). The design doc evolves
  between phases; an implementation that lines up with last week's
  reading of it is not the same as one that lines up with the
  current text. If the re-read surfaces a contradiction with the
  plan, fix the design first, then update the plan, then implement —
  never silently reconcile in code.
- **Stage → review → fix → commit → next.** Same gate the
  centralized-MCP migration used (see
  `architecture-centralized-mcp.md` § Migration Phases). No phase
  rolls into the next implicitly.
- **One branch per phase, named for the deliverable, not the phase
  number.** Use `features/<kebab-case-deliverable>` (e.g.
  `features/framework-restructure`,
  `features/engine-lifecycle-substrate`) — never
  `phase/NN-anything`. Phase numbers are a today-only coordinate
  that drifts when phases are split, merged, or reordered; the
  branch name should still make sense a year later when someone is
  reading `git log` cold. One phase = one branch = one PR, off the
  previous phase's merge commit (or off `main` when the phases are
  genuinely independent).
- **Several focused commits per branch; no squash on merge.** A
  phase rolls in as a sequence of small, behaviourally-coherent
  commits (one per logical sub-step — e.g. project split, shim
  removal, registration, rename), not one mega-commit. Hard rule:
  every individual commit compiles green and passes its tests via
  `.\scripts\prepare.ps1`; if a split can't satisfy that, the split
  is wrong, not the rule. Merge the PR with a merge commit or
  "Rebase and merge", never "Squash and merge" — squashing destroys
  the per-step bisect granularity that justified the focused
  commits in the first place. PR title mirrors the branch's
  deliverable as a Conventional Commits sentence (e.g.
  `refactor(framework): split into pipes/logging/protocol/workers`).
- **No versioning changes.** Version bumps are deliberate and
  user-driven (see `copilot-instructions.md` § Versioning). Phase
  branches do not touch `version.json`, `package.json`, or `.csproj`
  versions.
- **Conventional Commits** for every commit, with the relevant
  `.instructions.md` (`git-commit`, `lang-csharp`, `lang-typescript`,
  `testing`, `dotnet-async-await`, …) applied to the diff.

## Architectural anchors that must hold across every phase

These are properties the design treats as invariants. Every phase
review checks against them; a phase that breaks one rolls back
before merge.

- **P1**: one handler per capability; transports are marshalling
  shims. New surfaces register against the same handler the existing
  surfaces use; no business logic in the shim.
- **P2**: discriminated envelopes for state-bearing reads
  (`ok` / `disabled` / `not-found` / `*-error`), never nullables.
- **P3**: three decoupled representations — on-disk (authoring /
  generation), engine-internal (runtime snapshot), and wire (per-RPC
  projection); none dictates another's shape. On disk the curatorial
  layer is hand-authored (`instructions-catalog.json`) and the
  per-file facts are build-generated (`instructions-manifest.json`).
- **P4**: workspace identity is one hash; engine identity adds one
  per-launch UUID (fresh on every spawn; never reused across
  respawns). Endpoint names use the flat `<workspaceHash>#<instanceId>`
  segment (OS pipe namespaces are flat); on-disk paths use the
  nested `<workspaceHash>\<instanceId>` segments (POSIX: `/`).
  Never invent a parallel identifier; never flatten the two
  segments back into a workspace-only path.
- **P5**: on-disk path ownership is explicit and exclusive. New
  artefacts land in the table in `design § Distributed bundle layout`
  / `design § P5` with their owner declared.
- **P6**: subscriptions are first-class; clients never poll or watch.
- **P7**: two-layer matching — coarse on the engine, fine on the
  client.
- **P8**: async I/O end-to-end; no sync-over-async on hot paths.
- **P9**: concurrent reads, single-writer per resource,
  snapshot-immutable across reloads; per-subscriber bounded buffer
  with slow-subscriber drop on every `*.Subscribe`.
- **P10**: in-process async hooks are single-subscriber; cross-process
  fan-out is `*.Subscribe`. No classic .NET `event` slots in framework
  code.
- **P11**: the engine library splits into a **capability tier** and an
  **infrastructure substrate**. `Engine.Core/Features/` holds the
  outward-facing capabilities the extension consumes over RPC
  (`Instructions/` today; the `McpTools.*` capability is the next
  tenant) — the engine still boots without any of them, but without
  them nothing can consume anything. Everything outside `Features/` is
  infrastructure: required for the engine to run, whether a substantial
  subsystem (`Workspace/`, `Endpoints/`, `Machine/`) or plumbing
  (`Watchdogs/`, `Logging/`, `Infrastructure/`). A folder earns the
  `Features/` tier only when (a) the engine still runs without it *and*
  (b) it directly serves an outward consumer; `Workspace/` and the
  engine registry fail (b) and stay at root. The dependency arrow is
  one-way: capabilities may depend on the substrate, the substrate must
  never depend on a capability — a `using …Features.*` inside
  infrastructure is the smell that classification slipped.

Anything that adds an interface "for portability" needs a second
concrete implementation in the same phase or it doesn't ship. See
`design § Sharing principle`. Test fakes count as a second implementation
when the seam exists specifically to make the production path testable
(e.g. spawn-by-process vs. spawn-in-test); abstractions added for any
other reason still need a real second impl.

## Test strategy (applies to every phase)

- **Unit tests** run against the engine library composed in-process
  via `AddAutoContextEngine(...)` with a per-test workspace path and
  an overridden endpoint prefix (library-only `EngineOptions` knob
  documented in `design § Composition contracts`). This is the hot
  path — most phases live here.
- **Integration tests** spawn the published `autocontext-engine`
  binary against a temp workspace. Used for: pipe handshake / cold
  spawn, idle timeout, parent-pid watchdog, MCP-server-only role
  over stdio, cross-process `.autocontext.json` writes, packaging
  smoke. Phase 1 stands the integration harness up; subsequent
  phases extend it.
- **Test-project layout** mirrors the library layout (design §
  *Project layout*) one-to-one:
  `AutoContext.Framework.Pipes.Tests`,
  `AutoContext.Engine.Protocol.Tests`,
  `AutoContext.Workers.Core.Tests`,
  `AutoContext.Engine.Core.Tests` (absorbs today's
  `AutoContext.Mcp.Server.Tests` over the course of phases 7 and 15),
  `AutoContext.Client.Core.Tests`, `AutoContext.Engine.Tests`,
  `AutoContext.Instructions.Parser.Tests` (frontmatter + `applyTo`
  parser fixtures, round-trip invariant, section-index and cross-file
  reference-resolution coverage) and
  `AutoContext.Instructions.Manifest.Generator.Tests` (manifest
  builder + serializer assertions; the generator is also exercised
  end-to-end by the engine's build).
  Worker test projects are unchanged.
- **TS tests** stay in Vitest, in the same layout
  `AutoContext.Nodejs.Core` and `AutoContext.VsCode` already use.
- **Smoke tests** route through `scripts/test.ps1 -Smoke` as they do
  today.

## Target structure (end-state after Phase 15)

This is the shape the codebase converges to once every phase has
landed. Use it as a review anchor: each phase below moves the tree
*toward* this picture; nothing in the rollout should produce
intermediate shapes that aren't on a straight line to here. The
source of truth for the architectural rationale is
[`design § Project layout`](./autocontext-engine.md#project-layout)
and [`design § Distributed bundle layout`](./autocontext-engine.md#distributed-bundle-layout);
this section is the *contract* the implementation plan delivers.

### Scope

This document covers only the projects the `autocontext-engine`
rollout owns end-to-end:

- `AutoContext.Framework.Pipes/` — pipe transport primitives (split
  out of today's `AutoContext.Framework`).
- `AutoContext.Framework.Logging/` — *retired in Phase 8.* Held the
  legacy worker→extension sideband sink plus the `CorrelationScope`
  helper; the sideband was deleted and `CorrelationScope` moved into
  `AutoContext.Workers.Core` (next to its only consumer), emptying the
  project. The worker→engine log sender lives in
  `AutoContext.Workers.Core/Logging/`, and the canonical wire log
  envelope (`LogRecord`) is owned by `AutoContext.Engine.Protocol`.
- `AutoContext.Engine.Protocol/` — cross-side DTOs (the wire
  contract every RPC handler and typed dialer client marshals,
  including the canonical `LogRecord` envelope).
- `AutoContext.Workers.Core/` — worker-side runtime substrate
  (renamed from `AutoContext.Framework.Workers`): the
  `IMcpTask` contract (folded in from `AutoContext.Mcp.Abstractions`),
  `WorkerHostBuilderExtensions`, `WorkerTaskDispatcherService`,
  `WorkerHostOptions`, `WorkerHealthMonitorService` (hosted service
  that keeps the engine's `health` pipe connection open for the lifetime
  of the worker host), and the Phase 8 worker→engine log sender under
  `Logging/`. Depends on `Engine.Protocol` — it dials the engine, so the
  wire-contract dependency is correct.
- `AutoContext.Engine.Core/` — the engine itself as a library
  (every RPC family, the lifecycle hosted service, the stdio MCP-server
  role).
- `AutoContext.Engine/` — the binary host that publishes as
  `autocontext-engine[.exe]` and bundles the corpus + manifests next to
  the binary.
- `AutoContext.Client.Core/` — the dialler as a library (typed RPC
  clients, subscription consumers, find-or-spawn). Listed here because
  it is the in-process counterpart that exercises the engine's wire
  surface from .NET; the shared TS substrate in
  `AutoContext.Nodejs.Core/` is its sibling on the Node side
  (consumed by `AutoContext.VsCode` and `AutoContext.Worker.Web`).

Out of scope for this document (these projects are touched only to
adapt to the new engine, and their per-file shape lives in their own
plans):

- `AutoContext.Worker.*` — workers consume the
  `AutoContext.Workers.Core` worker-host scaffold; only their
  logger provider changes (it dials the engine's `rpc` pipe via the
  `Engine.WriteLog` RPC). The rest is carry-over.
  (`AutoContext.Mcp.Abstractions` and `AutoContext.Worker.Shared` are
  folded into the substrate projects as part of this rollout — see
  Phase 0; `IMcpTask` and `WorkerHostBuilderExtensions` both moved into
  `AutoContext.Workers.Core/` (then named `Framework.Workers`).
  The new `Engine.WriteLog`-side worker log sender lands in
  `AutoContext.Workers.Core/Logging/` in the engine-rollout phase that
  introduces it (Phase 8), not in Phase 0.)
- `AutoContext.VsCode` and `AutoContext.Nodejs.Core` (shared TS
  substrate) —
  pure consumers of the engine's wire surface.
- `AutoContext.CommandLine` — separate rollout, lives in
  [`autocontext-cli.md`](./future/autocontext-cli.md).

### Source tree

```
src/
  AutoContext.Framework.Pipes/                 # pipe transport primitives
    AutoContext.Framework.Pipes.csproj
    BoundPipeListener.cs
    PipeListener.cs                            # multi-connection server bind
    PipeTransport.cs
    LengthPrefixedFrameCodec.cs
    PipeKeepAliveClient.cs                     # rpc / events keep-alive dialer
    PipeStreamingClient.cs                     # logs / health passive consumer
    PipePersistentExchangeClient.cs
    PipeTransientExchangeClient.cs
    IPipeExchangeClient.cs

  AutoContext.Engine.Protocol/              # cross-side DTOs + endpoint shapes (leaf — no references)
    AutoContext.Engine.Protocol.csproj
    EndpointKind.cs                            # enum { Rpc, Events, Health, Logs } — the four logical channels per (workspace, launcher instance)
    Endpoint.cs                                # `readonly record struct` implementing IParsable<Endpoint> — builder + parser for rpc/events/health/logs × hash#instance
    WorkspaceHash.cs                           # 16-uppercase-hex SHA-256 prefix of the normalised workspace path — `readonly record struct` implementing `IParsable<WorkspaceHash>`; the `<workspaceHash>` segment of every Endpoint, on the shared leaf so the engine and every client derive it from one implementation (moved from Engine.Core in Phase 12)
    ServiceAddressFormatter.cs                 # legacy `autocontext.<role>#<instance-id>` formatter — kept until every current-topology dialer flips to Endpoint (Phase 12); deleted in Phase 15
    ProtocolVersion.cs                         # Engine.Hello version constant
    LogRecord.cs                               # canonical log-record envelope (timestamp, category, level, …)
    Envelopes/                                 # discriminated-envelope base shapes (P2)
      ResultEnvelope.cs                        # ok | disabled | not-found | *-error union root
      OkEnvelope.cs
      DisabledEnvelope.cs
      NotFoundEnvelope.cs
      ErrorEnvelope.cs
    Messages/                                  # per-RPC request/response DTOs
      EngineMessages.cs                        # Engine.Hello / Shutdown / WriteLog
      Lifecycle/                               # Engine.Lifecycle.Subscribe family (events-pipe notification payload)
        LifecycleMethods.cs                    # `Engine.Lifecycle` notification-method constant
        LifecycleEventKinds.cs                 # transition string constants (`started`, `shutting-down`, `reloading`, `dropped`)
        LifecycleEvent.cs                      # notification payload (Kind, InstanceId?, Revision?, Reason?)
      Registry/                                # Engine.RegistryEntries family (request method constant, result DTO, RegistryEntry record)
        RegistryMethods.cs                     # `Engine.RegistryEntries` wire-method constant
        RegistryEntriesResult.cs               # Engine.RegistryEntries response DTO
        RegistryEntry.cs                       # registry entry record (wire shape)
      ConfigMessages.cs                        # Config.{Get,Subscribe,ToggleFile,ToggleRule}
      InstructionsMessages.cs                  # Instructions.{List,Get,GetAll,GetAlwaysAttached,GetRaw,SearchContent,Subscribe}
      WorkspaceMessages.cs                     # Workspace.{Detect,Info}
      McpToolsMessages.cs                      # McpTools.{List,Invoke}
      DiscoveryMessages.cs                     # Discovery.{RouteForPrompt,RouteForTool}
      AgentMessages.cs                         # Agent.{SubagentStarted,SubagentStopped,Compacted,ToolUsed,TurnEnded} + Events.Subscribe
      LogsMessages.cs                          # Logs.{GetEngine,TailEngine,GetWorker,TailWorker}
    Serialization/
      ProtocolJsonContext.cs                   # source-generated System.Text.Json context for every DTO above

  AutoContext.Workers.Core/                    # worker-side runtime substrate: task contract + hosted services workers compose into their IHostBuilder, plus the worker→engine log sender. Refs Framework.Pipes + Engine.Protocol (it dials the engine, so depending on the wire contract is correct). Renamed from AutoContext.Framework.Workers.
    AutoContext.Workers.Core.csproj
    IMcpTask.cs                                # folded in from Mcp.Abstractions/
    WorkerHostBuilderExtensions.cs             # folded in from Worker.Shared/Hosting/
    WorkerTaskDispatcherService.cs             # moved from AutoContext.Framework/Workers/
    WorkerHostOptions.cs                       # moved from AutoContext.Framework/Workers/
    WorkerHealthMonitorService.cs              # hosted service that keeps the engine's health pipe connection open for the lifetime of the worker host
    CorrelationScope.cs                        # per-dispatch ambient correlation id (AsyncLocal); moved from the retired Framework.Logging in Phase 8
    Logging/                                   # Phase 8 — worker→engine log sender (folded from the old Worker.Shared/Logging; replaces the retired Framework.Logging legacy sideband)
      AddEngineLoggerProvider.cs               # wires the worker-side engine logger provider onto the host
      EngineLoggerProvider.cs                  # `ILoggerProvider` that marshals ILogger<T> records and dials Engine.WriteLog
      EngineLogIngestRing.cs                   # bounded in-memory ring (drop-oldest) + stderr drop fallback
      EngineWriteLogClient.cs                  # typed client for the Engine.WriteLog RPC (dials the engine rpc pipe, Engine.Hello handshake)

  AutoContext.Engine.Core/                # engine as a library
    AutoContext.Engine.Core.csproj
    EngineHostBuilderExtensions.cs             # IHostApplicationBuilder extension — composition root (AddAutoContextEngine)
    EngineMcpServerMode.cs                     # MCP-server capability mode selector (Off | WithStdioTransport)
    EngineOptions.cs                           # composition-time configuration — CLI knobs (--instance-id, --workspace-root, --idle-timeout, …) + library-only options
    EngineOptionsValidator.cs                  # validates EngineOptions shape against the documented ranges + charsets
    Infrastructure/                            # horizontal-axis substrate (cross-cutting plumbing); subdivided by kind, not by feature
      EngineResourcesDirectory.cs              # resolves the engine's Resources/ side-car directory, with an optional per-file override overlay
      EngineVersion.cs                         # resolves the running engine version from the AutoContext.Engine.Core assembly
      IUniqueInstanceGuard.cs                  # contract for the pre-bind "another engine already owns this <workspaceHash>#<instanceId>?" sanity check; production impl is Endpoints/PerWorkspaceInstanceGuard.cs
      IWorkspaceEngineInfo.cs                  # read-only view of engine metadata (workspace path, instance id, idle timeout) for services that need identity without taking EngineOptions
      Storage/                                 # cache-root vocabulary — identity coordinates and path resolution; leaf, consumed by Machine/ (EngineCacheLayout, Housekeeping) and Registry/ (RegistryEntryBuilder), depends on nothing engine-side itself
        CacheRoot.cs                           # per-instance identity bundle — composes EngineOptions into resolved cache-root subtree paths (FullPath / WorkspaceBucketPath / InstancePath / WorkspaceUserPath); the DI singleton every on-disk path resolves through
        CacheRootPathResolver.cs               # pure static — resolves the OS-level engine cache root (%LOCALAPPDATA%\autocontext, $XDG_CACHE_HOME/autocontext, …) with --cache-root override; sole reader of the env vars and override option
      Diagnostics/                             # System.Diagnostics.Process seam — internal abstractions used by Workers/ (launch + supervision), Watchdogs/, and registry-sweep liveness checks
        IProcess.cs                            # handle to a launched child process — pid + cancellable kill/exit operations
        IProcessHandle.cs                      # opens-once handle; exposes UTC start time and a cancellable WaitForExitAsync
        IProcessLauncher.cs                    # seam over OS process creation (Process.Start) for unit testability
        IProcessLookup.cs                      # TryOpen(pid) → handle | null (gone / denied); single seam over Process.GetProcessById
        IProcessObserver.cs                    # sink for a launched process's stderr lines + exit notification
        ProcessInfo.cs                         # immutable launch specification (command + arguments)
        ProcessLaunchException.cs              # thrown when a process cannot start or exits prematurely
        SystemProcessHandle.cs                 # production IProcessHandle wrapping System.Diagnostics.Process
        SystemProcessLookup.cs                 # production lookup; catches ArgumentException / InvalidOperationException / Win32Exception → null
      Events/                                  # fan-out substrate — the generic pub/sub core every stream (Lifecycle, Logs, Config, …) shares; domains add only a thin *StreamFrames framer (and, where they seed/terminate, a thin stream wrapper)
        Broadcaster.cs                         # singleton fan-out core `Broadcaster<TPayload>`: per-subscriber bounded Channel (capacity 64), slow-subscriber drop while the rest keep flowing, graceful Complete (clean EOF, no terminal frame); Subscribe(optional seed) → BroadcasterSubscription<T>
        SnapshotBroadcaster.cs                 # snapshot-on-subscribe wrapper over Broadcaster<T>: caches the latest published payload + Prime(seed) and replays it as the first frame to every new subscriber (post-Complete primes are dropped)
        BroadcasterSubscriber.cs               # per-subscriber bounded channel + Active/Closed/Dropped state machine (Interlocked CAS)
        BroadcasterSubscription.cs             # IDisposable handle returned by Subscribe; ReadAllAsync drains the channel; WasDropped exposes the terminal state the frame stream reads
        BroadcasterLog.cs                      # source-generated slow-subscriber-drop log messages shared by every stream
        IBroadcasterFrameStream.cs             # `IBroadcasterFrameStream<TPayload, TFrame>` — the subscription→frames contract every domain stream satisfies (StreamAsync)
        BroadcasterFrameStream.cs              # abstract base owning the shared drain/terminal-flush skeleton; subclasses supply only ToFrame + CreateDroppedFrame
        TrailingEdgeDebouncer.cs               # capacity-one channel + TimeProvider quiet window (P3 row 4); wrapped by Infrastructure/IO/FileChangeWatcher
      IO/                                      # filesystem-watch substrate
        FileChangeWatcher.cs                   # debounced FileSystemWatcher (via TrailingEdgeDebouncer) firing a callback on external edits; armed by the Config + Instructions-overrides watchers
    Endpoints/                                 # this engine's endpoint surface: the four-pipe host, the per-EndpointKind connection handlers, and the pre-bind instance guard
      EndpointHostService.cs                   # hosted service — binds the four pipes as an atomic unit and runs their accept loops; drives startup/registration/graceful-shutdown and hands each accepted connection to the IEndpointHandler registered for its EndpointKind (health is accept-and-close, so it has no handler)
      IEndpointHandler.cs                      # per-EndpointKind connection contract — Kind + HandleAsync(Stream, CancellationToken); the ct governs connection establishment only, while the events/logs writer pumps observe the shared ShutdownDrainDeadline once streaming starts
      RpcEndpointHandler.cs                    # rpc handler — runs the Engine.Hello handshake (HandshakePolicy) + idle keep-alive, then drives RpcConnectionProcessor against the shared singleton DispatchPolicy router
      EventsEndpointHandler.cs                 # events handler — handshake + keep-alive, then pumps LifecycleFrameStream frames as Engine.Lifecycle notifications until the drain deadline
      LogsEndpointHandler.cs                   # logs handler — passive (no handshake, no keep-alive); subscribes the log broadcaster and pumps NDJSON log frames until the drain deadline
      ShutdownDrainDeadline.cs                 # host-owned drain-deadline wrapper over a CancellationTokenSource (Reset on host start, StartDeadlineAsync to arm --shutdown-drain-timeout on stop, Release after drain); the seam the events/logs pumps watch so a graceful stop flushes terminal frames before teardown
      PerWorkspaceInstanceGuard.cs             # IUniqueInstanceGuard impl — dials the would-be `rpc` endpoint before bind; throws IOException when a live peer answers (P4 launcher-bug guard); not a hosted service
    Lifecycle/                                 # the Engine.Lifecycle.Subscribe events-stream domain (P10) — a thin layer over Infrastructure/Events/; holds no pipe-hosting code (that moved to Endpoints/)
      LifecycleEventStream.cs                  # singleton fan-out backing Engine.Lifecycle.Subscribe — wraps a shared Infrastructure/Events/Broadcaster<T>; layers on the `started` seed + terminal-event replay (Subscribe / TryPublish / TryComplete)
      LifecycleFrameStream.cs                  # BroadcasterFrameStream<JsonLifecycleEvent, JsonLifecycleEvent> (IBroadcasterFrameStream impl): drains a BroadcasterSubscription<JsonLifecycleEvent> and yields each event as a wire frame, emitting a terminal `dropped` frame when the subscriber was dropped
      LifecycleNotifier.cs                     # stamps the engine's identity (InstanceId, Revision) onto each transition and publishes through LifecycleEventStream — the stream itself constructs only the seeded `started` event
    Registry/                                  # engine-registry.json mechanics + this engine's own entry (its own tier — moved out of Lifecycle/)
      RegistryFileFormat.cs                    # stateless serializer + schema-version contract shared by reader and writer (envelope shape, JsonSerializerOptions)
      JsonRegistryEnvelope.cs                  # on-disk envelope DTO (schemaVersion + entries[]) for engine-registry.json
      RegistryFileReader.cs                    # concurrent-read surface for engine-registry.json (P9 concurrent reads); retry under FileShare.ReadWrite|FileShare.Delete + corrupt-file tolerance (returns empty list)
      RegistryFileReaderOptions.cs             # tunable read-retry backoff knobs (initial / max delay, multiplier, max-retries)
      RegistryFileWriter.cs                    # internal atomic single-shot writer; temp+fsync+rename only (no mutex, no retry, no RMW — owned by RegistryFileService)
      RegistryFileService.cs                   # hosted coordinator: dedicated worker thread + named cross-process Mutex + Channel<WriteRequest> + read-modify-write cycle; owns this engine's own-entry lifecycle (append on Start, best-effort remove on Stop); single intended caller of RegistryFileWriter
      RegistryFileServiceOptions.cs            # tunable cross-process mutex + worker-stop timeouts
      RegistryEntryBuilder.cs                  # pure builder — composes EngineOptions + runtime facts (pid, start time, workspace hash, assembly version) into the entry that represents this engine; invoked by RegistryFileService via DI-supplied factory
      RegistryEntryReader.cs                   # composes over RegistryFileReader; applies a Process.StartTime peer-liveness probe, tagging each entry Live/Stale — consumed by Machine/Housekeeping/ (CacheRootScanner) as the registration half of its classification
      RegistryEntryProbeResult.cs              # an entry paired with its liveness verdict (Live / Stale)
      RegistryEntryProbeState.cs               # enum: Live (pid + start-time match) | Stale (pid recycled / crashed)
    Watchdogs/                                 # process-lifetime guards — peers of Endpoints/; each is a hosted service that signals IHostApplicationLifetime.StopApplication on its own trigger
      IdleTimeoutWatchdog.cs                   # --idle-timeout
      HostWatchdog.cs                          # --parent-pid; clamps engine lifetime to spawner via Infrastructure/Diagnostics handle (Process.StartTime pid-reuse defeat)
      # NOTE: per-workspace unique-instance guard is NOT a watchdog (one-shot pre-bind probe, not a long-running monitor); see Endpoints/PerWorkspaceInstanceGuard.cs
    Machine/                                   # engine's on-disk residency: the cache-root subtree this engine owns and the housekeeping that walks the cache root as a whole; consumes Infrastructure/Storage vocabulary, owns no protocol surface of its own
      EngineCacheLayout.cs                     # single source of truth for every on-disk path the engine owns under its cache root (engine.log / crash.log + the shared registry file); composes off the CacheRoot singleton and freezes the resolved paths at construction
      EngineCrashWriter.cs                     # paranoid last-gasp writer of crash.log — sync File.AppendAllText, no DI, no ILogger, no async, allocation-light; wired into DaemonHostFactory.RunAsync top-level try/catch + AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException; never invoked from graceful shutdown paths
      Housekeeping/                            # cache-root upkeep: peer-registration liveness, orphan reaping, retention, foreign-subtree eviction (P5)
        HousekeepingService.cs                 # hosted service — shutdown sweep only, runs after EndpointHostService removes own entry + closes pipes; ≤ 1 s deadline budget
        SubtreeRegistryStatus.cs               # discriminated record hierarchy (Registered | StaleRegistration | Unregistered | Foreign) — P2-shaped contract between scanner, policy, and cleaner
        CacheRootScanner.cs                    # walks the engine cache root, produces SubtreeRegistryStatus per child (pure — no deletion here)
        StaleSubtreeCleaner.cs                 # pattern-matches SubtreeRegistryStatus, deletes with concurrent-sweep tolerance (DirectoryNotFoundException counts as success); applies Logging/RetentionPolicy per arm
    Logging/                                   # engine sink, rotation, rotated-file cleanup, retention, and log reads
      EngineLogger.cs                          # per-category ILogger — formats on the caller thread and enqueues to LogChannel
      EngineLoggerProvider.cs                  # ILoggerProvider caching one EngineLogger per category
      LogChannel.cs                            # single-channel ingest; TryWrite / ReadAllAsync / Complete (DropOldest on overflow)
      LogFileSinkService.cs                    # hosted service — drain loop + dispatcher; owns the per-target file appenders (engine.log / worker-<id>.log); also fans drained records out through a shared Infrastructure/Events/Broadcaster<JsonLogRecord> (pure live tail)
      LogRotationThresholds.cs                 # per-rotation-size line-count + byte-size rotation thresholds (replaces the old LogRotator)
      LogRotationSize.cs                       # rotation-selector enum (Small / Large)
      RotatedLogCleaner.cs                     # deletes rotated log files past retention inside a live subtree (uses Logging/RetentionPolicy)
      RetentionPolicy.cs                       # single reader of `--retention` — resolves the retention window (per-entry, unregistered-fallback, foreign); shared with Machine/Housekeeping/
      LogFileReader.cs                         # forward-pass NDJSON reader over the engine's per-instance log files with since / lastN filtering (backs Logs.GetEngine + Logs.GetWorker)
      EngineLogReadResult.cs                   # output record (Records, Truncated) of LogFileReader.ReadAsync
      LogFrameStream.cs                        # BroadcasterFrameStream<JsonLogRecord, JsonLogStreamFrame> (IBroadcasterFrameStream impl) for Logs.Tail*: drains a BroadcasterSubscription<JsonLogRecord> (fanned out by LogFileSinkService over the shared Infrastructure/Events/Broadcaster<T>) and yields record/dropped frames
      # Logs.{GetEngine,TailEngine} are served by Rpc/Handlers/LogsRpcHandler.cs (Logs.{GetWorker,TailWorker} not yet built)
    Workspace/                                 # workspace-scoped state — everything keyed by the current workspace root
      Config/                                  # .autocontext.json owner (Config.* wire surface)
        Snapshot/                              # immutable domain graph (engine-internal source of truth)
          ConfigSnapshot.cs                    # domain: root record + Empty
          ConfigEngineSettings.cs              # domain: engine settings record (instructions.overridesRoots)
          ConfigDiagnostic.cs                  # domain: diagnostic prefs record
          ConfigInstructionsFile.cs            # domain: per-instruction-file record (+ nested InstructionsRule)
          ConfigMcpTool.cs                     # domain: per-MCP-tool record
        Format/                                # on-disk wire DTOs (.autocontext.json shape)
          JsonConfigFile.cs                    # wire DTO: immutable on-disk config shape (P9)
          JsonConfigFileEngine.cs              # wire DTO: engine block (instructions.overridesRoots)
          JsonConfigFileDiagnostic.cs          # wire DTO: diagnostic block
          JsonConfigFileInstructionsEntry.cs   # wire DTO: instructions map entry (disabled, disabledRules)
          JsonConfigFileMcpToolEntry.cs        # wire DTO: mcpTools object entry (disabled)
        ConfigSnapshotExtensions.cs            # mapper: domain -> on-disk (ToFileFormat) + domain -> Config.* wire (ToWireFormat)
        JsonConfigFileExtensions.cs            # mapper: on-disk -> domain (ToDomainGraph)
        ConfigFileFormat.cs                    # stateless .autocontext.json serializer (mirrors RegistryFileFormat)
        ConfigFileManager.cs                   # store/manager — port of TS AutoContextConfigManager; owns the snapshot, FS-watch (Watch/ReconcileFromWatcherAsync), and signature-based self-write suppressor; implements IConfigSnapshotAccessor + IConfigUpdater
        ConfigFileService.cs                   # hosted service — initial disk load then arms the watcher at engine start
        IConfigSnapshotAccessor.cs             # lock-free read seam (Current) that ConfigRpcHandler reads for Config.Get
        IConfigChangeNotifier.cs               # change-notification seam (Changed event) the ConfigFileService bridges to the broadcaster
        ConfigBatchWriter.cs                   # micro-batch write coalescer behind IConfigUpdater (P3 row 6, DONE)
        IConfigUpdater.cs                      # one-method write seam the manager satisfies (P3 row 6, DONE)
        ConfigFrameStream.cs                   # BroadcasterFrameStream<JsonConfigSnapshot, JsonConfigStreamFrame> (IBroadcasterFrameStream impl) for Config.Subscribe: drains a BroadcasterSubscription<JsonConfigSnapshot> (fanned out by ConfigFileService over a shared Infrastructure/Events/SnapshotBroadcaster<T> — snapshot-on-subscribe + per-subscriber bounded buffer, P3 row 9, DONE) and yields snapshot/dropped frames; Config.{Subscribe,Get,ToggleFile,ToggleRule} are served by Rpc/Handlers/ConfigRpcHandler.cs (the two toggles via IConfigUpdater)
      Context/                                 # ~60-flag detection (Workspace.* wire surface)
        WorkspaceDetectionService.cs           # hosted service — scans the workspace at startup and re-runs on watched changes, publishing the result
        WorkspaceContextDetector.cs            # detector core — injected with the three rule-data lists below; runs them, emits a WorkspaceDetectionResult
        WorkspaceFileClassifier.cs             # compiles the rule tables into lookup indices (extension/filename dicts + glob list)
        WorkspaceFileEnumerator.cs             # recursive workspace walker with directory pruning + per-entry resilience
        IWorkspaceContextAccessor.cs           # read-only accessor exposing the latest WorkspaceDetectionResult
        WorkspaceDetectionResult.cs            # immutable outcome — the FrozenSet of raised flag names
        WorkspaceDetectionResultExtensions.cs  # projects the result onto the Workspace.Detect wire shape
        # Workspace.{Detect,Info} are served by Rpc/Handlers/WorkspaceRpcHandler.cs
        # — Rule data (plain records; each file holds a `static readonly`
        #   table registered in DI as the corresponding `IReadOnlyList<T>`
        #   singleton; no interfaces — substitution is over the data, not
        #   the behaviour) —
        FileSelector.cs                        # (Value, FileSelectorKind) — one selection criterion, shared by presence + content
        FileSelectorKind.cs                    # enum: Extension | FileName | GlobPattern
        FilePresenceRule.cs                    # one row of IReadOnlyList<FilePresenceRule> — selectors → flag (presence only)
        ContentScan.cs                         # one row of IReadOnlyList<ContentScan> — manifest selectors + ContentPatternRule list (npm, .NET, …)
        ContentPatternRule.cs                  # (Flag, Regex) — body-pattern → flag, scoped under a ContentScan
        FlagActivationEdge.cs                  # one row of IReadOnlyList<FlagActivationEdge> — [child, parent] transitive activation graph
        WorkspaceDetectionRules.cs             # static partial holding the three tables (FileRules, ContentScans, FlagActivationEdges) + GeneratedRegex patterns
        # — Derived indices (built from the rule tables) —
        FlagExtensionIndex.cs                  # maps each flag to its extension-selector set (fed to Discovery, P7)
        FlagContributionIndex.cs               # inverted index — files → base flags and flags → contributor counts
    Features/                                  # outward-facing capability tier (P11): served to the extension over RPC; the engine runs without these, but without them nothing can consume anything. Instructions/ + McpTools/ are built; Discovery/ + Agent/ (below) are not yet
      Instructions/                            # runtime services for the Instructions.* surface
        InstructionsManifestService.cs           # hosted service — loads the merged catalog+manifest snapshot at startup
        IInstructionsManifestAccessor.cs         # read-only accessor over the loaded corpus snapshot
        InstructionsManifestLoader.cs            # reads Resources/instructions-catalog.json + instructions-manifest.json, merges into the snapshot
        InstructionsOverridesService.cs          # hosted service — scans the configured override directories at startup
        IInstructionsOverridesAccessor.cs        # read-only accessor over the override inventory
        InstructionsOverridesWatcher.cs          # per-overrides-root instructions/ FS watcher (debounced, default .github); syncs the override inventory
        InstructionsOverridesStalenessInspector.cs # emits a warning when an override file is older than its bundled counterpart
        InstructionsBodyProjector.cs             # projects a manifest entry's body per request: resolves override-vs-bundled, reads + parses; response body filters disabled rules + slices sections for Get ([INSTxxxx] tags preserved), search body rebuilds the offset-bearing body for indexing
        InstructionsFileReader.cs                # reads the verbatim body from the bundled or override source
        InstructionsListProjector.cs             # projects manifest + overrides + config into the Instructions.List rows
        InstructionsResponseBody.cs              # Get output (content + returned-sections + not-found-sections)
        InstructionsSearchBody.cs                # indexed body (content + sections with offsets)
        InstructionsFullTextSearchService.cs     # in-memory full-text search over instruction bodies
        InstructionsSubscriptionService.cs       # hosted service — primes the broadcaster and bridges config edits into Instructions.Subscribe
        InstructionsFrameStream.cs               # BroadcasterFrameStream for Instructions.Subscribe: drains a BroadcasterSubscription (fanned out over a shared Infrastructure/Events/SnapshotBroadcaster<T> — snapshot-on-subscribe + disabled-flag re-evaluation) and yields snapshot/dropped frames
        # Instructions.{List,Get,GetAll,GetAlwaysAttached,GetRaw,SearchContent,Subscribe} are served by Rpc/Handlers/InstructionsRpcHandler.cs
        Snapshot/                              # immutable snapshots + section/manifest records
          InstructionsManifestSnapshot.cs        # immutable merged corpus snapshot
          InstructionsOverridesSnapshot.cs       # immutable overrides-root inventory (paths + basenames)
          InstructionsFileManifestEntry.cs       # per-file manifest entry (description, checksum, sections)
          InstructionsCategoryEntry.cs           # catalog category entry
          InstructionsSection.cs                 # section anchor (name + TextSpan offset range)
        Format/                                # on-disk catalog/manifest DTOs + source-gen context
          JsonInstructionsCatalog.cs / JsonInstructionsCatalogCategory.cs / JsonInstructionsCatalogEntry.cs
          JsonInstructionsManifest.cs / JsonInstructionsManifestEntry.cs / JsonInstructionsManifestSection.cs
          InstructionsManifestJsonContext.cs     # System.Text.Json source-generation context
      McpTools/                                # runtime services for the McpTools.* surface (built — was the doc's old top-level Mcp/ folder)
        IMcpToolsRegistryAccessor.cs             # read-only accessor over the loaded tool-registry snapshot
        McpToolsRegistryService.cs               # hosted service — loads the registry at startup
        McpToolsRegistryLoader.cs                # reads Resources/mcp-tools-registry.json and validates it via the schema validator
        McpToolsRegistrySchemaValidator.cs       # validates the registry against its schema + cross-reference rules
        McpToolsCatalogSchemaValidator.cs        # validates mcp-tools-catalog.json against its schema
        McpToolsRegistryValidationResult.cs      # validation outcome with ordered error messages
        IMcpToolsInvoker.cs                      # dispatch seam for McpTools.Invoke
        McpToolsInvoker.cs                       # production invoker — dispatches a tool call to the owning worker (lazy-spawn via Workers/WorkerProcessService)
        McpToolsInvokerNoop.cs                   # no-op fallback returning a deterministic tool-error (kept for composition; production now injects McpToolsInvoker directly)
        EditorConfig/                          # per-request .editorconfig enrichment for McpTools.Invoke
          IEditorConfigResolver.cs               # resolver seam invoked before tool dispatch
          WorkerEditorConfigResolver.cs          # default resolver — round-trips to Worker.Workspace
        Snapshot/                              # immutable registry snapshot records
          McpToolsRegistry.cs / McpToolsRegistryEntry.cs / McpToolsRegistryParameterEntry.cs / McpToolsCategoryEntry.cs
        Format/                                # on-disk registry/catalog DTOs + source-gen contexts
          JsonMcpToolsRegistry.cs / JsonMcpToolsRegistryTool.cs / JsonMcpToolsRegistryParameter.cs
          JsonMcpToolsCatalog.cs / JsonMcpToolsCatalogCategory.cs / JsonMcpToolsCatalogTool.cs
          McpToolsRegistryJsonContext.cs / McpToolsCatalogJsonContext.cs
        # McpTools.{List,Invoke} are served by Rpc/Handlers/McpToolsRpcHandler.cs
      # — Features NOT YET BUILT (P11 capabilities; kept here as the Phase target) —
      Discovery/                               # category & extension routing indices (P7 — Phase 9, NOT YET BUILT). A capability→capability read of the Instructions + McpTools snapshots (allowed; only substrate→capability is forbidden)
        DiscoveryService.cs                    # lazy-built category→tool + extension→file indices (structural indices are immutable at runtime); reads IConfigSnapshotAccessor.Current per query for the disabled filter
        CategoryIndex.cs                       # inverts McpToolsRegistryEntry.Category (ancestry-inclusive) → prompt category-word scan → MCP tools
        ExtensionIndex.cs                      # InstructionsFileManifestEntry.Extensions → prompt extension scan → instructions files
        # Discovery.{RouteForPrompt,RouteForTool} are served by Rpc/Handlers/DiscoveryRpcHandler.cs
      Agent/                                   # Agent.* RPC family (P10 — NOT YET BUILT)
        AgentEventFrameStream.cs               # BroadcasterFrameStream<AgentEvent, …> (IBroadcasterFrameStream impl) for Events.Subscribe: drains a BroadcasterSubscription<AgentEvent> (fanned out over a shared Infrastructure/Events/Broadcaster<T> — pure live tail, bounded per-subscriber buffers + drop) and yields event/dropped frames
        AgentSessionToolHistogram.cs           # in-memory per-session ToolUsed counts
        # Agent.* notifications + Agent.Events.Subscribe are served by Rpc/Handlers/AgentRpcHandler.cs
    Workers/                                   # worker process lifecycle (absorbs AutoContext.Mcp.Server/Workers/)
      WorkerProcessService.cs                  # lazy manager — EnsureRunningAsync(workerId) gate; spawns on first use, respawns on exit (was WorkerManager)
      WorkerProcessLauncher.cs                 # production launcher — starts a worker via System.Diagnostics.Process (over Infrastructure/Diagnostics seams)
      WorkerProcess.cs                         # production IProcess — wraps the worker Process with stderr/stdout drainage
      WorkerProcessInfo.cs                     # launch spec — extends Infrastructure/Diagnostics/ProcessInfo with endpoint + workerId
      WorkerProcessInfoResolver.cs             # maps a JsonWorkersManifest row to a WorkerProcessInfo launch spec
      IWorkerConnectionProbe.cs                # readiness contract — confirms a worker is accepting on its pipe (replaces the old stderr ready-marker scrape)
      WorkerConnectionProbe.cs                 # production probe — polls the worker endpoint with backoff until the pipe answers
      WorkersManifestLoader.cs                 # reads Resources/workers.json
      Format/                                # workers.json on-disk DTOs
        JsonWorkersManifest.cs                 # disk model (workers[])
        JsonWorkerEntry.cs                     # disk model (id, type, label, command with ${root} placeholder)
        WorkersManifestJsonContext.cs          # System.Text.Json source-generation context (camelCase)
    # — NOT YET BUILT (kept here as the Phase target) —
    McpServer/                                 # stdio MCP-server role (P11) — a transport shim, sibling of Rpc/ + Endpoints/, NOT a Feature: it owns no capability logic, it re-exposes Features/Instructions + Features/McpTools over MCP stdio, depending on Features/* exactly as Rpc/Handlers/ do
      McpSdkAdapter.cs                         # generic router: aggregates IMcpToolSource tools for tools/list, routes tools/call by name to the matching IMcpTool leaf, maps the response to CallToolResult. Knows no concrete tools.
      StdioMcpServerEntryPoint.cs              # composes AddMcpServer().WithStdioServerTransport() — the stdio host (a future HttpMcpServerEntryPoint would be the sibling transport)
      InputSchemaBuilder.cs                    # renders each tool's JSON-schema inputSchema for tools/list
      McpServerHostBuilderExtensions.cs        # AddMcpServer — the reduced-but-sufficient DI composition for the role
      McpStdioStartupLoader.cs                 # one-shot hosted service: initial config reload + workspace DetectAsync (no watcher)
      Tools/                                   # the flat tool model — leaves + sources grouped by nature; add a tool/family here, never in the adapter
        IMcpTool.cs                            # one tool: its Descriptor + InvokeAsync(itself). No routing.
        IMcpToolSource.cs                      # produces the IMcpTool leaves for one family
        HandlerMarshaller.cs                   # marshals a tools/call into an IRpcMethodHandler and returns its JsonRpcResponse
        JsonArguments.cs                       # reads tools/call args by name+kind (shared by the leaves)
        Intrinsics/                            # in-process, engine-native tools (no worker)
          InstructionsListTool.cs / InstructionsSearchContentTool.cs / InstructionsGetTool.cs   # instructions_* leaves, shimming over Instructions.*
          InstructionsToolSource.cs            # the instruction family (the fixed set of instruction leaves)
        Registry/                              # worker-backed tools, data-driven from mcp-tools-registry.json
          RegistryMcpTool.cs                   # one analyze_*/read_* leaf per registry entry, dispatching through McpTools.Invoke
          RegistryToolSource.cs                # the worker-backed family (one leaf per mcp-tools-registry entry)
      # NOTE: the per-request .autocontext.json reload is exposed as IConfigReloader implemented by ConfigFileManager (Workspace/Config/) — no separate reader class. AutoContext.Engine/McpServerHostFactory composes this host for the --mcp-server with-stdio role (P11/P12).
    Rpc/                                       # pipe-side connection processing + the policy/result framing shared by every handler
      RpcConnectionProcessor.cs                # per-connection loop — reads frames, routes via the active IRpcConnectionPolicy, writes responses
      IRpcConnectionPolicy.cs                  # strategy contract — handshake gate, frame-failure policy, and method→handler table for a connection
      Continuation.cs                          # enum: Continue (next frame) | Exit (success) | ExitFailure (error)
      FrameFailurePolicy.cs                    # enum: Recover (reply JSON-RPC error, keep reading) | Disconnect
      JsonRpcId.cs                             # JSON-RPC 2.0 id normalization + null-handling helpers
      Policies/                              # the two connection policies
        HandshakePolicy.cs                     # enforces the mandatory Engine.Hello handshake at the pipe head
        DispatchPolicy.cs                      # post-handshake JSON-RPC dispatch — a stateless shared-singleton pure router: composes the injected IRpcMethodHandler set into a method→handler map and dispatches each request; owns only Engine.Shutdown inline (MethodNotFound when no handler claims the method)
      Handlers/                              # per-feature RPC method handlers — each owns its own feature deps and is DI-registered as an IRpcMethodHandler the DispatchPolicy router composes
        IRpcMethodHandler.cs                   # contract — Methods (the wire-method names it serves) + InvokeAsync(JsonRpcRequest, CancellationToken)
        RpcMethodResults.cs                    # shared result builders — Success<T> / InvalidParams / InternalError + TryDeserialize<T> (params-parse failure → InvalidParams with a logged reason)
        ConfigRpcHandler.cs                    # Config.{Get,ToggleFile,ToggleRule,Subscribe}
        InstructionsRpcHandler.cs              # Instructions.{List,Get,GetAll,GetAlwaysAttached,GetRaw,SearchContent,Subscribe}
        LogsRpcHandler.cs                      # Logs.{GetEngine,TailEngine}
        McpToolsRpcHandler.cs                  # McpTools.{List,Invoke}
        RegistryRpcHandler.cs                  # Engine.RegistryEntries
        WorkspaceRpcHandler.cs                 # Workspace.{Detect,Info}
      Results/                               # handler outcome shapes the processor flushes
        RpcHandlerResult.cs                    # base — carries a Continuation + optional post-flush side effect
        UnaryHandlerResult.cs                  # single JsonRpcResponse frame
        StreamingHandlerResult.cs              # JsonRpcStreamNext frames + a terminal Complete/Error

  AutoContext.Client.Core/                # in-process .NET dialler library (consumed by CLI, .NET tests, future .NET embedders)
    AutoContext.Client.Core.csproj
    ClientHostBuilderExtensions.cs             # IHostApplicationBuilder extension — composition root (AddAutoContextClient)
    ClientOptions.cs                           # workspace path, instance-id, instance-label, spawn policy, idle-timeout pass-through
    ClientOptionsValidator.cs                  # validates ClientOptions shape (path rootedness, non-empty instance id, label charset)
    Engine/                                    # everything that dials the engine — spawn seam + RPC layer
      IEngineSpawner.cs                        # spawn seam — production = process spawn, tests = in-proc fake
      EngineSpawner.cs                         # production IEngineSpawner — Process.Start against the bundled binary (was ProcessEngineSpawner)
      EngineSpawnRequest.cs                    # immutable launch spec — workspace, instance id, label, idle-timeout, binary path
      EngineLocator.cs                         # static AppContext.BaseDirectory probe for the bundled engine binary
      EngineConnectBudget.cs                   # warm + cold connect timing / backoff shape
      Rpc/                                     # connection primitives + typed clients (one per surface)
        EngineConnection.cs                    # one handshaked pipe — Engine.Hello + serialised ExchangeAsync seam
        EngineConnector.cs                     # find-or-spawn resolver — warm dial, spawn, cold-retry, handshake
        EngineProtocolException.cs             # raised on Hello version mismatch / refusal
        EngineUnavailableException.cs          # raised when no engine is reachable and spawning is disabled or timed out
        EngineRpcClient.cs                     # Engine.Hello/Shutdown/RegistryEntries (NOT WriteLog — worker→engine, Workers.Core owns)
        ConfigRpcClient.cs
        InstructionsRpcClient.cs
        WorkspaceRpcClient.cs
        McpToolsRpcClient.cs
        DiscoveryRpcClient.cs
        AgentRpcClient.cs
        LogsRpcClient.cs
      Subscriptions/                           # IAsyncEnumerable<T> consumers (P6, P8)
        EngineLifecycleSubscription.cs
        ConfigSubscription.cs
        InstructionsSubscription.cs
        AgentEventsSubscription.cs
        LogsTailSubscription.cs

  AutoContext.Instructions.Parser/        # shared parser library (net10.0) — referenced by both the generator and the engine runtime so one source is compiled for both
    AutoContext.Instructions.Parser.csproj     # TargetFramework=net10.0; class library
    InstructionsFileSyntaxParser.cs            # streaming lexer → InstructionsFileSyntaxTree: frontmatter + body span streams plus reference + diagnostic side streams, with whole-file source coordinates
    InstructionsFileFactory.cs                 # disk entry point: FromFileAsync → InstructionsFileSyntaxParser tree → Model.InstructionsFile.FromSyntaxTree
    FrontmatterApplyToParser.cs                # applyTo splitter/brace-expander — parse only, round-trip-verified
    InstructionsFileReferenceResolver.cs       # pure cross-file resolver — validates rule/section references against an InstructionsFileCatalog (no I/O)
    Syntax/                                    # syntax layer: InstructionsFileSyntaxTree (frontmatter/body span streams + reference + diagnostic side streams) + InstructionsFileSyntaxSpan + coordinate structs + Kind/EmitLevel/EmitScope/Diagnostic(Kind) enums
    Model/                                     # structured model: InstructionsFile (RawContent/Frontmatter/Body/References/Diagnostics) rebuilt from the syntax tree (InstructionsFileBody.WithoutTaggedRules reparses for disabled-rule projection), plus section/rule/reference + catalog/finding records

  AutoContext.Instructions.Manifest.Generator/   # build-time console generator (net10.0, AssemblyName instructions-manifest-gen) — not shipped with the engine
    AutoContext.Instructions.Manifest.Generator.csproj   # OutputType=Exe; ProjectReference → AutoContext.Instructions.Parser
    InstructionsManifestGenerator.targets      # imported by AutoContext.Engine.csproj; <Exec>s the generator during the binary's build
    Program.cs                                 # generic-host entry point (build host → resolve runner → Run → exit code)
    InstructionsManifestGenerator.cs           # runner: reads instructions-catalog.json, scans src/AutoContext.Engine/Instructions/, writes instructions-manifest.json into the binary's Resources/
    InstructionsCatalogReader.cs               # reads + cross-validates the hand-authored instructions-catalog.json (categories, label, membership, activationFlags)
    InstructionsManifestBuilder.cs             # corpus scan + per-file facts (sections, applyTo ext set, hashes) → JsonInstructionsManifest
    InstructionsManifestSerializer.cs          # deterministic, byte-stable JSON writer (no System.Text.Json dependency)
    # the engine sequences the generator via a ProjectReference (ReferenceOutputAssembly=false);
    # the target runs AfterTargets=ResolveProjectReferences BeforeTargets=CoreCompile.

  AutoContext.Workers.Manifest.Generator/      # build-time console generator (net10.0, AssemblyName workers-manifest-gen) — not shipped with the engine
    AutoContext.Workers.Manifest.Generator.csproj        # OutputType=Exe
    WorkersManifestGenerator.targets           # imported by AutoContext.Engine.csproj; <Exec>s the generator during the binary's build
    Program.cs                                 # generic-host entry point (build host → resolve runner → Run → exit code)
    WorkersManifestGenerator.cs                # runner: scans src/, writes workers.json into the binary's Resources/
    IWorkerDescriptorScanner.cs                # scanner contract
    WorkerDescriptorScanner.cs                 # selects worker projects by the presence of .autocontext-worker.json and reads id/type/label/command verbatim; an AutoContext.Worker.* project without a descriptor fails the build, and duplicate ids are rejected
    IWorkersManifestSerializer.cs              # serializer contract
    WorkersManifestSerializer.cs               # deterministic, byte-stable JSON writer
    JsonWorkersManifest.cs                     # manifest DTO
    JsonWorkerEntry.cs                         # per-worker DTO
    WorkersManifestJsonContext.cs              # source-generated System.Text.Json context
    WorkersManifestJsonOptions.cs              # shared serializer options
    WorkersManifestGeneratorServiceCollectionExtensions.cs  # DI registration
    # sequenced exactly like the instructions generator: ProjectReference with
    # ReferenceOutputAssembly=false, target AfterTargets=ResolveProjectReferences BeforeTargets=CoreCompile.

  AutoContext.Engine/                          # engine binary host
    AutoContext.Engine.csproj                  # publishes as autocontext-engine[.exe]
    Program.cs                                 # entry point — wires the System.CommandLine parser with a diagnostic prefix
    EngineCommand.cs                           # RootCommand describing the CLI surface (the switches + daemon vs. --mcp-server role split; replaces the old ArgvParser/Role/StartupBanner trio)
    DaemonHostFactory.cs                       # composes IHostBuilder → AddAutoContextEngine for the daemon role, with the unhandled-exception crash sinks
    McpServerHostFactory.cs                    # composes the stripped --mcp-server with-stdio host (currently a stub — not yet implemented)
    Instructions/                              # bundled corpus — copied next to the binary,
      <curated *.instructions.md files>       # resolved via AppContext.BaseDirectory
                                               # (not embedded resources)
    Resources/                                 # read-only side-cars — copied next to the binary
      instructions-catalog.json                #   hand-authored curatorial layer (tracked in source)
      instructions-catalog.schema.json         #   JSON-schema for the catalog
      instructions-manifest.json               #   build-generated per-file facts (P3)
      mcp-tools-registry.json                  #   hand-authored: what each tool is for the model + dispatch (flat tools[])
      mcp-tools-registry.schema.json           #   JSON-schema for the registry
      mcp-tools-catalog.json                   #   hand-authored: when each tool activates + where it sits in the UI
      mcp-tools-catalog.schema.json            #   JSON-schema for the catalog
      workers.json                             #   generated from the per-worker .autocontext-worker.json descriptors

  tests/
    AutoContext.Framework.Pipes.Tests/         # transport primitives — listener, codec, keep-alive, exchange/streaming triad
    AutoContext.Engine.Protocol.Tests/      # DTO envelope round-trips (including LogRecord), endpoint builder, source-generated JSON contexts
    AutoContext.Workers.Core.Tests/            # IMcpTask, WorkerHostBuilderExtensions, WorkerTaskDispatcherService, WorkerHealthMonitorService, CorrelationScope, worker→engine log sender
    AutoContext.Engine.Core.Tests/             # engine-internal services + every RPC handler + lifecycle + watchdogs
    AutoContext.Client.Core.Tests/             # typed RPC clients, subscription consumers, find-or-spawn flow
    AutoContext.Engine.Tests/                  # binary-host integration: argv parser, role split, ready-marker, end-to-end spawn
    AutoContext.Instructions.Parser.Tests/     # frontmatter + applyTo parser fixtures, round-trip invariant
    AutoContext.Instructions.Manifest.Generator.Tests/  # manifest builder + serializer assertions
    AutoContext.Workers.Manifest.Generator.Tests/       # descriptor scanner + serializer assertions
    AutoContext.Framework.Tests.Support/       # shared test-support reused by engine + worker tests
```

Worker projects, the MCP-abstractions project, the VS Code extension,
and the shared TS substrate (`AutoContext.Nodejs.Core/`) are consumers of the
surfaces defined above; their per-file shape stays in their own
documents and is not enumerated here.

**One type per file.** Each `*.cs` filename above names exactly one
top-level type (class, record, enum, or interface). The RPC handlers
follow the same rule: each RPC family is a single
`Rpc/Handlers/<Family>RpcHandler` implementing `IRpcMethodHandler`
(one class per family, one file per class). `Rpc/Policies/DispatchPolicy`
is a stateless shared-singleton pure router — it composes the injected
handler set into a method-name → handler map and owns only
`Engine.Shutdown` inline; `Rpc/RpcConnectionProcessor.cs` drives the
per-frame loop against the active `IRpcConnectionPolicy`. The
handler-per-family shape (cohesion by RPC family rather than one class
per RPC method) is the deliberate trade-off: cohesion over file count,
matched to the connection-policy dispatcher and to the rest of the
codebase's vertical-feature folder axis.

> **Renames since this plan was first written.** The source tree above
> reflects the *current* code; several types were renamed or
> reorganised after their phases landed. The historical commit-subject
> rows in the phase tables below keep their original wording, so this
> map bridges the two:
>
> - `WorkerManager` → `Workers/WorkerProcessService` (lazy spawn gate);
>   `WorkerControlClient` → `Workers/WorkerConnectionProbe`; supervisor
>   + task-dispatch responsibilities folded into `WorkerProcessService`
>   / `WorkerProcessLauncher` and `Features/McpTools/McpToolsInvoker`.
> - `HelloHandler` / `ShutdownHandler` → `Rpc/Policies/HandshakePolicy`
>   + the `Engine.Shutdown`-inline path on `DispatchPolicy`;
>   `RpcDispatcher` → `RpcConnectionProcessor` + `IRpcConnectionPolicy`.
>   `DispatchPolicy` was briefly a partial-per-family class, then split
>   back into one `Rpc/Handlers/<Family>RpcHandler` (`IRpcMethodHandler`)
>   per RPC family (`ConfigRpcHandler`, `InstructionsRpcHandler`,
>   `LogsRpcHandler`, `McpToolsRpcHandler`, `RegistryRpcHandler`,
>   `WorkspaceRpcHandler`), leaving `DispatchPolicy` a pure router.
> - `LifecycleService` → `Endpoints/EndpointHostService` (the four-pipe
>   host); the per-EndpointKind connection handlers
>   (`RpcEndpointHandler`, `EventsEndpointHandler`, `LogsEndpointHandler`)
>   and `PerWorkspaceInstanceGuard` moved from `Lifecycle/` into the new
>   `Endpoints/` tier, leaving `Lifecycle/` as the events-stream domain.
> - The engine registry moved out of `Lifecycle/` into its own
>   `Registry/` tier; the `Mcp/` folder became `Features/McpTools/`.
> - `AddAutoContextEngine.cs` → `EngineHostBuilderExtensions.cs` (the
>   `AddAutoContextEngine` extension method name is unchanged);
>   `ArgvParser` / `Role` / `StartupBanner` → `EngineCommand`;
>   `LogRotator` → `LogRotationThresholds` (+ `LogRotationSize`);
>   `FileExtensionsIndex` → `FlagExtensionIndex` (+ `FlagContributionIndex`).
> - **Project rename:** `AutoContext.Framework.Workers` →
>   `AutoContext.Workers.Core` (worker-side runtime). Renamed so no
>   `Framework.*` project depends on `Engine.*`: the worker-side runtime
>   legitimately dials the engine, so depending on `Engine.Protocol` is
>   correct once the project sheds the `Framework.` prefix. The design
>   doc (`docs/autocontext-engine.md`) still uses the older working
>   names `Framework.Protocol` (now `Engine.Protocol`) and
>   `Framework.Services` (now `AutoContext.Workers.Core`); the code and
>   this tree are authoritative. The worker→engine log sender
>   (`AddEngineLoggerProvider` + `EngineLoggerProvider` +
>   `EngineLogIngestRing` + `EngineWriteLogClient`) lives in
>   `Workers.Core/Logging/`, **not** `Framework.Logging`. `Framework.Logging`
>   itself was retired in Phase 8: after its legacy sideband was deleted it
>   held only `CorrelationScope`, which moved into `Workers.Core`, so the
>   emptied project (and its test project) were removed.

### Runtime bundle layout (shipped artefact)

Per the design's distributed-bundle picture: every shipped host
artefact (VSIX per platform, plugin release per platform,
GitHub-release tarball per RID) embeds the same `engine/` subtree.
The per-RID segment that exists at build-staging time
(`artifacts/engine/<rid>/…`) is **absent** from the shipped product.

```
<host bundle root>/
  engine/
    autocontext-engine[.exe]                   # the binary (one role, two modes)
    <self-contained .NET runtime files>        # dotnet publish -r <rid> --self-contained output
    Instructions/                              # curated corpus side-cars
    Resources/                                 # read-only side-cars (mirror of src tree above)
      instructions-catalog.json
      instructions-manifest.json
      mcp-tools-registry.json
      mcp-tools-registry.schema.json
      mcp-tools-catalog.json
      mcp-tools-catalog.schema.json
      workers.json
    Workers/
      workspace/<entrypoint>                   # one self-contained subdir per worker
      dotnet/<entrypoint>
      web/<entrypoint>
```

Resolution at runtime uses `AppContext.BaseDirectory`; no
host-supplied path threads into the engine for side-car lookup.

### Wire surfaces by end-state owner

| Surface | Owner project | Transport |
|---|---|---|
| `Engine.Hello` / `Shutdown` / `RegistryEntries` / `Lifecycle.Subscribe` | `Engine.Core` | `rpc` + `events` |
| `Engine.WriteLog` | `Engine.Core` | `rpc` |
| `Logs.GetEngine` / `TailEngine` / `GetWorker` / `TailWorker` | `Engine.Core` | `rpc` + `logs` |
| `Config.Get` / `Subscribe` / `ToggleFile` / `ToggleRule` | `Engine.Core` | `rpc` + `events` |
| `Workspace.Detect` / `Info` | `Engine.Core` | `rpc` |
| `Instructions.List` / `Get` / `GetAll` / `GetAlwaysAttached` / `GetRaw` / `SearchContent` / `Subscribe` | `Engine.Core` | `rpc` + `events` |
| `McpTools.List` / `Invoke` (pipe RPC) | `Engine.Core` | `rpc` |
| MCP `tools/list` / `tools/call` (stdio) | `Engine.Core` (MCP-server role) | stdio |
| `Discovery.RouteForPrompt` / `RouteForTool` | `Engine.Core` | `rpc` |
| `Agent.*` notifications + `Agent.Events.Subscribe` | `Engine.Core` | `rpc` + `events` |
| Typed .NET clients for every row above | `Client.Core` | dial-side |
| Typed TS clients for every row above (plus TS-side engine-daemon lifecycle) | `Nodejs.Core/src/engine/engine-daemon-manager.ts` | dial-side |

## Phase 0 — Framework restructure

**Status**: Completed on branch `features/framework-restructure`.

| # | Commit subject | State |
|---|---|---|
| 1 | `refactor(framework): split into pipes/logging/protocol/workers` | DONE |
| 2 | `refactor(mcp): fold IMcpTask into Framework.Workers` | DONE |
| 3 | `refactor(workers): fold WorkerHostBuilderExtensions into Framework.Workers` | DONE |
| 4 | `refactor(tests): split Framework.Tests across substrate projects` | DONE |
| 5 | `refactor(ts): rename Framework.Web to Nodejs.Core` | DONE |
| 6 | `docs(plan): correct Worker.Shared fold scope` | DONE |
| 7 | `docs(plan): mark Phase 0 complete` | DONE |

**Goal**: reshape the existing project graph into the four-project
`Framework.*` substrate the rest of the rollout consumes, fold the
two dead-weight projects (`Mcp.Abstractions`, `Worker.Shared`) into
it, and rename the shared TS substrate to its end-state identity.
This phase touches existing code only — every new engine / client /
instructions-tooling project is created in the phase that first uses it (see
*Just-in-time scaffolding* in the ground rules).

**Design anchors**: `§ Project layout` (Framework substrate row),
`§ Composition contracts`.

**Code touch**:
- Split today's `AutoContext.Framework` project into four sibling
  projects under `src/` (one mechanical pass; touches every
  `<ProjectReference>`, `RootNamespace`, and `using`):
  - `AutoContext.Framework.Pipes/` — receives the existing
    `AutoContext.Framework/Pipes/` files unchanged.
  - `AutoContext.Framework.Logging/` — receives the existing
    `AutoContext.Framework/Logging/` files. References
    `Framework.Pipes` + `Framework.Protocol`.
  - `AutoContext.Engine.Protocol/` — new sub-project (no
    equivalent in today's substrate). Skeletons for the cross-side
    DTOs (protocol-version constant, endpoint builder, log-record
    envelope, discriminated-envelope base shapes, source-generated
    JSON context). Also receives `AutoContext.Framework/Workers/ServiceAddressFormatter.cs`
    — it's a pure endpoint string-formatting helper (no I/O, no
    lifetime, no DI), the same wire-shape concern `Endpoint.cs`
    owns under the engine topology; parking the legacy formatter next
    to its successor keeps both endpoint shapes in one place and
    avoids a misleading lineage in Phase 1 where the engine's builder
    would otherwise materialise in a different project than the
    formatter it eventually replaces. Leaf — no other Framework
    references.
  - `AutoContext.Framework.Workers/` — receives
    `AutoContext.Framework/Workers/{WorkerTaskDispatcherService,WorkerHostOptions}.cs`
    and `AutoContext.Framework/Hosting/HealthMonitorClient.cs`
    (renamed in-flight to `WorkerHealthMonitorService.cs` as part of
    the move — the type is an `IHostedService` scoped to the worker
    host's lifetime, not a call-site dialer, so the `*Client` suffix
    mis-cued against the BCL convention where `*Client` reads as
    `HttpClient`-shaped, and the `Worker*` prefix pins what its
    lifetime is actually tied to). References `Framework.Pipes` +
    `Framework.Logging` + `Framework.Protocol`.
  - The empty `AutoContext.Framework` shell project is deleted
    once its files have been redistributed.
- Rename the shared TS substrate project `AutoContext.Framework.Web` →
  `AutoContext.Nodejs.Core` (folder, `package.json` `name`, internal
  imports, every `tsconfig`/`vitest`/`build.ps1` path reference).
  The rename drops the `.Web` suffix — misleading because it
  suggests browser/HTTP — and pairs the shared TS substrate with
  its .NET siblings under a per-runtime `.Core` shape; `Nodejs`
  names the runtime both consumers (`AutoContext.VsCode` extension
  host and `AutoContext.Worker.Web`) actually run on. The unrelated
  `AutoContext.Worker.Web` worker project keeps its name.
- Fold two adjacent projects into the four `AutoContext.Framework.*`
  sub-projects (no behaviour change; pure project-graph
  simplification):
  - `AutoContext.Mcp.Abstractions` (one file: `IMcpTask.cs`) →
    `AutoContext.Framework.Workers/IMcpTask.cs`. Delete the
    `AutoContext.Mcp.Abstractions` project.
  - `AutoContext.Worker.Shared` (one file:
    `Hosting/WorkerHostBuilderExtensions.cs`) →
    `AutoContext.Framework.Workers/`. Delete the
    `AutoContext.Worker.Shared` project.
  - Every `Worker.*` project drops its `Mcp.Abstractions` and
    `Worker.Shared` `<ProjectReference>`s and picks up
    `<ProjectReference>`s to all four `AutoContext.Framework.*`
    projects directly.
- New test projects, one per new Framework sub-project:
  `AutoContext.Framework.Pipes.Tests`,
  `AutoContext.Framework.Logging.Tests`,
  `AutoContext.Engine.Protocol.Tests`,
  `AutoContext.Framework.Workers.Tests`.
  Today's `AutoContext.Framework.Tests` is split across the four
  substrate test projects according to which sub-project owns each
  fixture. Test projects for the *new* engine / client / instructions-tooling
  projects come up alongside those projects in their first-use
  phases.
- `AutoContext.slnx` updated for the four Framework sub-projects,
  the renamed `Nodejs.Core`, and the deletions of `Mcp.Abstractions`
  / `Worker.Shared`. No entries for engine / client / instructions-tooling
  projects yet — those are added by the phases that introduce them.
- `build.ps1` learns the new Framework project list (compile targets
  only; packaging stays out until Phase 13).

**Tests**:
- Solution builds via `.\build.ps1`.
- All existing `Worker.*` tests and the split-up Framework substrate
  tests stay green after the rename + consolidation (no behaviour
  change — the diff is purely namespace + project-graph).

**Out of scope**: every new engine / client / instructions-tooling project
(introduced in their first-use phases); any pipe binding, DI
registration, or executable host.

## Phase 1 — Engine lifecycle substrate

**Status**: Completed on branch `features/engine-lifecycle-substrate`.

| # | Commit subject | State |
|---|---|---|
| 1 | `docs(engine): rename pipe names to endpoints` | DONE |
| 2 | `feat(protocol): add Endpoint and ProtocolVersion` | DONE |
| 3 | `feat(engine-core): scaffold project with composition root and options` | DONE |
| 4 | `feat(engine): scaffold binary host with role-split argv parser` | DONE |
| 5 | `feat(engine-core): add RegistryFile{Reader,Writer,Format} single-writer owner of engine-registry.json` | DONE |
| 5b | `refactor(engine-core): make RegistryFileWriter a single-worker hosted service with named-mutex coordination and atomic temp-file writes` | DONE |
| 6 | `feat(engine-core): add LifecycleService four-pipe accept loops` | DONE |
| 7 | `feat(engine-core): add Engine.Hello handshake and protocol-version gate` | DONE |
| 8 | `feat(engine-core): add RegistryEntryBuilder and own-entry lifecycle on RegistryFileService` | DONE |
| 9 | `feat(engine): serve Engine.RegistryEntries and Engine.Shutdown over rpc` | DONE |
| 10 | `feat(engine-core): add Engine.Lifecycle.Subscribe events stream and notifier` | DONE |
| 10b | `test(engine): align lifecycle test conventions` | DONE |
| 10c | `fix(engine): bound lifecycle shutdown drain by configurable timeout` | DONE |
| 10d | `refactor(engine): unify rpc handshake and dispatch behind RpcConnectionProcessor` | DONE |
| 11 | `feat(engine-core): add idle-timeout watchdog` | **DONE** |
| 12 | `feat(engine-core): add host watchdog` | **DONE** |
| 13 | `feat(engine-core): add unique-instance guard` | **DONE** |
| 14 | `feat(engine): wire EngineCrashWriter to sinks` | **DONE** |
| 15 | `test(engine): stand up integration harness for binary spawn` | **DONE** |
| 16 | `docs(plan): mark Phase 1 complete` | **DONE** |

**Goal**: engine binds the four pipes, performs the `Engine.Hello`
handshake, manages its own idle/parent-pid/shutdown lifecycle, and
participates in the shared liveness registry.

**Design anchors**: `§ Lifecycle`, `§ Engine options (CLI surface)`,
`§ RPC surface` (`Engine.Hello`, `Engine.RegistryEntries`,
`Engine.Shutdown`, `Engine.Lifecycle.Subscribe`), `§ P4`, `§ P5`,
`§ P8`.

**Code touch**:
- **Create `AutoContext.Engine.Core/`** — new class library, the
  engine as a library. References `Framework.Pipes` +
  `Framework.Logging` + `Framework.Protocol`. Public surface:
  `IHostApplicationBuilder.AddAutoContextEngine(Action<EngineOptions>)`
  composing the hosted services listed below.
- **Create `AutoContext.Engine/`** — new binary project. References
  `AutoContext.Engine.Core` (created in the bullet above). Program
  entry point implements the full argv parser per `§ Engine options`
  (daemon-role table) including `--version`, strict rejection of
  unknown switches with a one-line stderr error.
- **Create sibling test projects** `AutoContext.Engine.Tests` and
  `AutoContext.Engine.Core.Tests` alongside the projects above.
- `AutoContext.slnx` and `build.ps1` learn the two new projects
  (and their test siblings).
- `AutoContext.Engine.Protocol/` — endpoint builder (workspace
  hash + `<kind>` + `<instanceId>`; normalisation rules in `§ Endpoint`),
  protocol-version integer.
- `AutoContext.Framework.Pipes/` — used as-is. The existing
  `PipeListener` / `BoundPipeListener` pair already delivers the
  atomic-bind, multi-connection accept loop, pre-bound continuous
  listening, and drain-on-cancel semantics the engine needs; no new
  transport seams are required for the four-pipe bind.
- `AutoContext.Engine.Core/` — hosted services for: pipe accept
  loops (`rpc`, `events`, `health`, `logs` — `logs` is bound here so
  consumers see EOF cleanly, but engine record emission lives in
  Phase 2), `Engine.Hello` handler, `Engine.Lifecycle` broadcaster,
  `Engine.RegistryEntries` handler, `Engine.Shutdown` handler,
  the own-entry lifecycle folded into `RegistryFileService` (append
  on Start, best-effort remove on Stop — composing
  `RegistryEntryBuilder` for the pure construction half),
  idle-timeout watchdog, parent-pid watchdog.
- `RegistryFileReader` / `RegistryFileWriter` / `RegistryFileFormat`
  / `RegistryFileService` — sole owners of `engine-registry.json`,
  split along three orthogonal concerns so `§ P9`'s
  single-writer-per-resource rule is enforced by *type identity*
  and `§ P8`'s end-to-end async stays honest at the public API
  even though the OS `Mutex` primitive demands thread affinity:
  (a) `RegistryFileFormat` owns the envelope shape, schema-version
  contract, and `JsonSerializerOptions` (stateless, shared by
  reader and writer); (b) `RegistryFileReader` is the passive
  concurrent-read surface — opens with
  `FileShare.ReadWrite | FileShare.Delete` so it coexists with the
  writer's atomic rename, retries under exponential backoff, and
  tolerates corrupt/unknown-schema files by returning an empty
  list; (c) `RegistryFileWriter` is an `internal` atomic
  single-shot writer — temp+fsync+rename only, no mutex, no retry,
  no RMW — small enough that its only correctness obligation is
  that the real file is replaced atomically; (d) `RegistryFileService`
  is the public hosted coordinator that owns *all* complexity:
  a single dedicated background thread runs a fully synchronous
  worker loop (so the named cross-process `Mutex`'s acquire/release
  affinity is satisfied by construction), a `Channel<WriteRequest>`
  serialises in-process callers, a session-local
  `AutoContext.RegistryFile.{sha256(path)[..16]}` named mutex
  serialises cross-process peers (the registry lives under the
  per-user cache root, so session-local scope matches the
  contention surface; a `Global\` prefix would require
  `SeCreateGlobalPrivilege` and falsely couple unrelated users),
  and a per-request
  `TaskCompletionSource` keeps the `WriteAsync` API honestly async.
  The service handles `AbandonedMutexException` gracefully
  (atomic-rename writer guarantees no torn intermediate state, so
  reclaiming the mutex is always safe). Every consumer (this phase's
  own-entry lifecycle on `RegistryFileService`, Phase 2b's
  `RegistryEntryReader`, any future peer-watcher) composes over
  the appropriate surface: writers go through
  `RegistryFileService`, readers go through `RegistryFileReader`. Born in Phase 1 because Phase 1 is when
  `engine-registry.json` is first written; Phase 2b composes over
  `RegistryFileReader` rather than reaching into the file directly.
- `engine-registry.json` entry lifecycle per
  `§ Housekeeping` and the `engine-registry.json entry lifecycle`
  pitfall: append-on-start (fresh `instanceId` every spawn; no
  upsert), remove-on-graceful-shutdown, leave-stale-on-crash. The
  locking, `FileShare.None` writer window, atomic temp+rename, and
  exponential-backoff reader retry are owned by the reader/writer
  pair above; this bullet pins the *lifecycle* of the entry, not
  the file mechanics.
- The same-`instanceId`-collision rule (`§ Lifecycle` *Concurrent
  first-connect*) — a second engine binding under the same
  `--instance-id` is a launcher bug under the per-launch-UUID
  contract (P4); the engine fails loudly on pipe-bind collision
  with a non-zero exit. `PerWorkspaceInstanceGuard` (the sole
  production `IUniqueInstanceGuard` impl, called at the top of
  `EndpointHostService.StartAsync` before any pipe bind) is the
  fail-fast sanity check enforcing this contract; it probes the
  would-be `rpc` endpoint and throws `IOException` when a peer
  already holds the address. The design does **not** treat the
  collision as a shape bind has to be idempotent against.

**Tests** (unit + integration):
- `Engine.Hello` protocol-version exact-match acceptance and refusal.
- `events` pipe requires the `Hello` envelope before any subscription.
- `health` and `logs` accept connections without the handshake (raw
  read; passive observer rule).
- Four pipes bind atomically; clients can dial each independently.
- Idle timeout fires after the configured window with no `rpc`/`events`
  connections; `--idle-timeout 0` disables the gate.
- `--parent-pid <pid>` watchdog exits cleanly when the parent process
  vanishes; pid-recycling defeat via `Process.StartTime` comparison.
- Registry entry appended on start, removed on graceful shutdown,
  left stale on crash. Two graceful starts of the *same* launcher
  (each minting a fresh `<instanceId>`) leave two distinct entries
  in the registry while both are live; each engine removes its own entry
  on shutdown.
- A crashed engine's entry survives; the next graceful shutdown of
  any peer reaps it after the entry's `retention` window elapses.
- Two engines starting concurrently with the *same* `--instance-id`:
  both fail loudly on pipe-bind collision with non-zero exit — this
  is a launcher-bug fixture, not a normal-operation shape. The
  engine emits a diagnostic log line naming the colliding pipe and
  writes a `crash.log` tombstone under its per-instance subtree
  describing the collision.
- `EngineCrashWriter` produces a parseable `crash.log` under
  `…\<workspaceHash>\<instanceId>\logs\` when an unhandled
  exception escapes `DaemonHostFactory.RunAsync`, when a non-main
  thread raises via `AppDomain.UnhandledException`, and when an
  unobserved `Task` faults; graceful `Engine.Shutdown`,
  idle-timeout, and parent-pid watchdog exits produce **no**
  `crash.log`. A deliberately broken write target (read-only
  directory) does not mask the original fault — the process still
  exits with the original non-zero code.
- Corrupt-file recovery: an unparseable
  `engine-registry.json` is truncated and re-seeded by the next start.
- `Engine.Shutdown` returns `{ accepted: true }` immediately, drains
  `rpc`, emits `shutting-down` on `events`, closes pipes, exits 0.
- Integration: spawn the binary, dial each pipe, verify handshake +
  shutdown.

**Out of scope**: cache-root housekeeping sweep — peer-registration
liveness scan, orphan reaping, retention enforcement, foreign-subtree
eviction (Phase 2; needs the nested `<workspaceHash>\<instanceId>`
per-instance subtree shape Phase 2 introduces alongside logging).
Rotating log file production (`engine.log`, `worker-<workerId>.log`;
Phase 2) — note that `crash.log` is in-scope for Phase 1 because
the `EngineCrashWriter` it depends on is wired up here. Worker spawn
(Phase 7).

## Phase 2 — Engine logging pipeline and cache housekeeping

**Status**: Completed on branch `features/engine-logging-and-housekeeping`.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(protocol): introduce LogRecord wire envelope` | DONE |
| 2 | `feat(engine-core): add log ingest channel and engine.log file writer` | DONE |
| 3 | `feat(engine-core): route engine ILogger<T> through ingest channel via EngineLoggerProvider` | DONE |
| 4 | `feat(engine-core): add RetentionPolicy and log rotation with RotatedLogCleaner` | DONE |
| 5 | `feat(engine-core): fan out engine records on logs pipe with per-subscriber buffer and slow-subscriber eviction` | DONE |
| 6 | `feat(engine): serve Logs.GetEngine over rpc` | DONE |
| 7 | `feat(engine-core): add RegistryEntryReader` | DONE |
| 8 | `feat(engine-core): add SubtreeRegistryStatus and CacheRootScanner` | DONE |
| 9 | `feat(engine-core): add StaleSubtreeCleaner and HousekeepingService shutdown sweep` | DONE |
| 10 | `test(engine): integration test for cross-engine shutdown-sweep cleanup` | DONE |
| 11 | `docs(plan): mark Phase 2 complete` | DONE |

Two equal-tier features land together because they share the
per-instance subtree shape (both write under it) and the
`engine-registry.json` reader (`RegistryEntryReader` consults the
same entries `RegistryFileService` produces). Neither is subordinate to
the other; each gets its own subsection below. Rows 1–6 implement
2a (engine logging pipeline); rows 7–10 implement 2b (cache
housekeeping). Row 4's `RetentionPolicy` is the shared seam both
features compose over, so it must land before row 9.

### 2a — Engine logging pipeline

**Goal**: every record the engine emits via `ILogger<T>` lands in
`engine.log` under the per-instance subtree, fans out on the `logs`
pipe and `Logs.TailEngine` RPC subscribers, rotates per `--log-rotation`,
and rotated files are cleaned per `--retention`.

**Design anchors**: `§ Log categories`,
`§ RPC surface` (`Logs.GetEngine`, `Logs.TailEngine`,
`Engine.WriteLog` envelope shape), `§ P9` (slow-subscriber drop),
`§ Log pipeline backpressure` pitfall.

**Code touch**:
- `AutoContext.Engine.Protocol/LogRecord.cs` — the canonical wire
  envelope (`timestamp`, `category`, `level`, `eventId?`, `message`,
  `properties?`, `exception?`). Phase 2a collapses today's substrate
  pair `LogEntry`/`JsonLogEntry` into this single record under
  Protocol's ownership; `Framework.Logging` keeps the worker-side
  logger provider and the legacy sideband sink, but the envelope
  itself moves to where every other cross-side DTO lives (P1: one
  record envelope; P3: wire shape owned by Protocol).
- `AutoContext.Engine.Core/Logging/` — engine-side log sink:
  one ingest channel (`LogChannel`), one drain loop
  (`LogFileSinkService`) that dispatches each drained record to
  inner sinks — the file sink (today only; routes to
  `engine.log` / `worker-<id>.log` from row 8 by `category`
  prefix) and, from row 5, the `logs`-pipe / `Logs.Tail*`
  broadcaster (per-subscriber bounded buffer; slow subscribers
  dropped with a terminal
  `{ kind: "dropped", reason: "slow-subscriber" }` frame). Row 5
  reshapes `LogFileSinkService` from "drain-and-write" into
  "drain-and-dispatch" so the broadcaster sits next to the file
  sink as a sibling inner sink rather than as a second consumer
  of `LogChannel` (the channel stays single-reader).
- Rotation per `--log-rotation` thresholds (1k lines / 5 MB small; 5k /
  25 MB debug); rotated-file naming `engine-<iso8601>.log`.
- `RotatedLogCleaner` deletes rotated files older than the
  `--retention` window during the next rotation. (Per-tenant
  cleanup inside a *live* subtree; whole-subtree cleanup is
  Housekeeping's job — see 2b. The two share `RetentionPolicy`
  as their single reader of the `--retention` option.)
- `Logs.GetEngine` handler (active file only; `opts.lastN`,
  `opts.since`, `truncated` flag). `crash.log` is intentionally
  **out of scope** for the `Logs.*` RPC surface: it is a write-once
  tombstone produced by Phase 1's `EngineCrashWriter`, not a
  tail-able feed, and is reaped along with the rest of the
  per-instance subtree by 2b housekeeping under `--retention`.
- `Logs.TailEngine` is **deferred to the Phase 3 prelude** — it
  needs a server-streaming-response convention on the `rpc` pipe
  (today the dispatcher is strictly one-request → one-response),
  and that convention is shared infrastructure with
  `Config.Subscribe` / `Instructions.Subscribe` in Phase 3. Building
  it once, on the cusp of Phase 3, avoids landing unused streaming
  plumbing in Phase 2. Interim consumers tail the `logs` pipe
  directly (raw NDJSON, no handshake) per the design's documented
  fallback.

**Tests**:
- Engine `ILogger<T>` records hit `engine.log` and the `logs` pipe
  with the same envelope shape; the wire shape and the on-disk shape
  match byte-for-byte (P1: one record envelope).
- Rotation fires at line-count and at size threshold; both `normal`
  and `debug` thresholds tested.
- `--retention` removes rotated files older than the window during
  the next rotation.
- `Logs.GetEngine` returns active-file content; `truncated: true`
  when the requested range fell off.
- Slow-subscriber drop (against the row-5 `logs`-pipe / future
  `Logs.Tail*` broadcaster): a subscriber that doesn't drain gets
  the terminal `dropped` frame and is disconnected; other
  subscribers and the file sink keep progressing.

### 2b — Cache housekeeping

**Goal**: the engine cache root is self-cleaning. Every live engine
classifies the cache root on startup (before pipe bind) and again
on shutdown (after removing its own registry entry), and reaps every
subtree that isn't backed by a live peer entry, subject to the
`--retention` floor. Housekeeping is a first-class engine feature,
not a logging chore.

**Design anchors**: `§ Housekeeping` (the two-clock schedule, the
≤ 1 s deadline budget, the concurrent-sweep contract),
`§ Engine-owned on-disk artefacts` pitfall, `§ P5` (path ownership
is explicit and exclusive).

**Code touch**:
- `AutoContext.Engine.Core/Machine/Housekeeping/HousekeepingService` —
  hosted service that runs the **shutdown sweep only**. No startup
  sweep: under the per-launch-UUID contract (P4) every engine's
  `<instanceId>` is fresh on every spawn, so the registry stays
  append-only and there is nothing to reconcile before pipe-bind.
  Cleanup of any peer's leftover subtree happens at this engine's
  own graceful shutdown, after `RegistryFileService` has removed
  this engine's own entry (via its own `StopAsync` prefix) and
  `EndpointHostService` has closed the four pipes. Hosted-service
  registration order pins the invariant: register
  `HousekeepingService` **after** `RegistryFileService` (and
  before `EndpointHostService`) so its `StopAsync` runs *before*
  the file service's — reverse-registration order — letting
  the sweep traverse the still-live `RegistryFileService.WriteAsync`
  channel before it's torn down, while still observing the
  on-disk registry in its post-pipe-close shape. Bounded
  by the ≤ 1 s deadline the design specifies; whatever the sweep
  doesn't reach this time, the next graceful shutdown of any
  peer catches.
- `SubtreeRegistryStatus` — discriminated record hierarchy
  (`Registered` | `StaleRegistration` | `Unregistered` | `Foreign`)
  carrying the on-disk subtree path and any registry-entry data
  downstream consumers need. The closed-set classification is
  `§ P2`-shaped (state-bearing read over a finite set of
  possibilities) and is the contract between three consumers:
  `CacheRootScanner` produces it, `RetentionPolicy` resolves the
  window per arm, `StaleSubtreeCleaner` pattern-matches to act. The
  type lets each consumer be tested independently; promoting it from
  an internal switch to a public-shape type also gives diagnostics
  per arm ("reaped N stale-registered, M foreign") for free.
- `CacheRootScanner` — walks the engine cache root and produces a
  `SubtreeRegistryStatus` for each child. Pure: no deletion, no
  policy decisions here. Owns the file-system walk and the registry
  lookup against `RegistryFileReader` (Lifecycle/, Phase 1). The
  four classification arms:
    1. **Registered** — backed by an `engine-registry.json` entry
       whose pid is alive (`Process.StartTime` defeats pid recycling);
    2. **StaleRegistration** — backed by an entry whose pid is dead
       or recycled;
    3. **Unregistered** — a nested `<workspaceHash>\<instanceId>`
       subtree (matches the canonical shape) but no registry entry
       claims it;
    4. **Foreign** — anything that doesn't match the canonical
       nested per-instance shape: legacy flat
       `<workspaceHash>#<instanceId>` directories from before the
       nested layout, bare `<workspaceHash>` directories from even
       earlier preview builds, or any other shape under the cache
       root. Because the cache root is engine-owned (P5), a foreign
       subtree is by definition stale.
- `RegistryEntryReader` — composes over `RegistryFileReader`
  (Lifecycle/, Phase 1) to read all entries, applies the
  `Process.StartTime` peer-liveness check, and supplies the
  registration half of `CacheRootScanner`'s classification. The file
  mechanics (retry, corrupt-file tolerance) live in
  `RegistryFileReader` — this entry reader only adds the liveness
  check on top of the entry data it gets back.
- `StaleSubtreeCleaner` — pattern-matches over
  `SubtreeRegistryStatus`, asks `RetentionPolicy` for the window
  per arm, deletes when outside the window. Concurrent-sweep
  tolerance: a `DirectoryNotFoundException` mid-walk counts as
  success (a peer engine won the race).
- `RetentionPolicy` — the *single* place that reads `--retention`.
  Resolves the window per `SubtreeRegistryStatus` arm: per-entry
  windows honour the entry's own retention if present; unregistered
  and foreign subtrees fall back to this engine's `--retention`.
  Both 2a's `RotatedLogCleaner` and 2b's `StaleSubtreeCleaner` take
  their window from this type (no scattered `EngineOptions.Retention`
  reads).

**Tests**:
- Shutdown sweep classifies a populated cache root correctly across
  all four `SubtreeRegistryStatus` arms against a synthetic
  registry file.
- Stale-registration subtree is deleted once outside the entry's
  retention, preserved while still inside it.
- Unregistered subtree falls back to this engine's `--retention`.
- Foreign subtree (legacy flat `<workspaceHash>#<instanceId>` from
  before the nested layout, or bare `<workspaceHash>` from even
  earlier preview builds) is deleted once it exceeds the retention
  floor, preserved while still inside the retention window.
- Two engines concurrent sweep on the same stale subtree: one wins,
  the other treats `DirectoryNotFoundException` as success; neither
  faults.
- Pid recycling: a registry entry whose pid is now held by an
  unrelated process is correctly classified as stale via
  `Process.StartTime` comparison.
- The ≤ 1 s deadline budget is respected: a deliberately huge cache
  root yields after the budget elapses without blocking shutdown.
- Shutdown sweep runs after `RegistryFileService` has removed this
  engine's own entry (during its own `StopAsync`) and after
  `EndpointHostService` has closed the four pipes — a peer that
  starts mid-shutdown does not observe this engine's entry as live.
- Integration: spawn two engines against the same cache root,
  hard-kill one (skipping its shutdown sweep), then gracefully
  shut down the survivor; assert the survivor reaps the killed
  engine's subtree as part of its own shutdown sweep, and that no
  live subtree was touched.

**Out of scope** (2a): worker records (Phase 8); `Logs.GetWorker`
/ `Logs.TailWorker` (Phase 8); `Logs.TailEngine` (deferred to the
Phase 3 prelude — see row 6 code touch above). 2b has no
out-of-scope carve-out;
its dependency on Phase 1's `RegistryFileReader` /
  `RegistryFileWriter` and `RegistryFileService` (which owns both
  the file mechanics and this engine's own-entry lifecycle, and
  supplies the on-disk `engine-registry.json` entries the reader
  surfaces) is declared under code touch, not deferred work.

## Phase 3 — Config store

**Status**: Completed on branch `features/config-store`.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(engine-core): support rpc server-streaming` (prelude) | DONE |
| 2 | `feat(engine): serve Logs.TailEngine over rpc` (prelude) | DONE |
| 3 | `feat(engine-core): port AutoContextConfigManager to AutoContextConfig` | DONE |
| 4 | `feat(engine-core): add FileChangeWatcher with trailing-edge debounce reload` | DONE |
| 5 | `feat(engine-core): add deep-equal self-write suppressor` | DONE |
| 6 | `feat(engine-core): add writer mutex and micro-batch write coalescing` | DONE |
| 7 | `feat(engine): serve Config.Get over rpc` | DONE |
| 8 | `feat(engine): serve Config.ToggleFile and Config.ToggleRule over rpc` | DONE |
| 9 | `feat(engine-core): add Config.Subscribe events stream with snapshot-on-subscribe` | DONE |
| 10 | `test(engine): integration test for cross-instance config reload coalescing` | DONE |
| 11 | `docs(plan): mark Phase 3 complete` | DONE |

**Landed-row notes.**

- **Row 4 — `FileChangeWatcher` with trailing-edge debounce.**
  Extracted a reusable `TrailingEdgeDebouncer` (capacity-one channel
  + `TimeProvider`-scheduled quiet window) into `Infrastructure/Events`,
  wrapped by a `FileChangeWatcher` in `Infrastructure/IO` that forwards
  `FileSystemWatcher` events as signals; the manager's
  `ReconcileFromWatcherAsync` → `RefreshAsync` runs once per settled
  burst. Manager ctor gains `timeProvider` / `debounceDelay`
  (default 100&#160;ms). Deterministic `FakeTimeProvider` tests via
  `Microsoft.Extensions.TimeProvider.Testing`.
- **Row 5 — deep-equal self-write suppressor.** Landed in row 3,
  signature-based: SHA-256 of the on-disk bytes compared against
  `_lastSignature`.
- **Row 6 — writer mutex and micro-batch write coalescing.** Added a
  `ConfigBatchWriter` that the manager owns and exposes through an
  additive `UpdateBatchAsync` API, leaving the synchronous
  `UpdateAsync` primitive untouched. An unbounded single-reader
  channel drains queued edits, a `TimeProvider`-scheduled micro-batch
  window (default 5&#160;ms) folds every edit that lands inside it
  into one write call (one write, one snapshot swap, one fan-out),
  and each caller's task completes when its batch is applied. The
  writer depends on a one-method `IConfigUpdater` seam the manager
  satisfies directly (its existing `UpdateAsync` matches), keeping the
  two decoupled and unit-testable against a fake. Per-edit
  cancellation drops the edit from its batch; `Dispose` cancels
  in-flight edits. Deterministic `FakeTimeProvider` tests. The
  revision counter and `{ revision, changes }` envelope stay deferred
  to rows 8-9.
- **Row 7 — serve `Config.Get` over rpc.** The snapshot reaches
  clients without a streaming subscription: `ConfigFileManager`
  exposes its lock-free `Current` snapshot through an
  `IConfigSnapshotAccessor` seam, and `ConfigFileService` (a hosted
  service registered before `EndpointHostService`) performs the initial
  disk load and arms the watcher at engine start so the snapshot is
  populated before the first request can land. `ConfigRpcHandler`
  serves `Config.Get`, projecting the current snapshot
  onto the `Config.*` wire DTOs via
  `ConfigSnapshotExtensions.ToWireFormat`. The revision counter and
  the `Config.Subscribe` fan-out stay deferred to rows 8-9.
- **Row 8 — serve `Config.ToggleFile` / `Config.ToggleRule` over
  rpc.** Both methods flip state (matching the `Toggle` name): a pure
  `ConfigToggle` transform takes the current `ConfigSnapshot` and
  returns a new one with the targeted instruction file's whole-file
  disabled flag (`ToggleFile`) or a single rule's disabled flag
  (`ToggleRule`) flipped — creating the entry when absent and pruning
  it when the edit leaves it carrying no state, so the in-memory graph
  matches a reload. `DispatchPolicy` routes both methods to unary
  handlers that validate params (rejecting missing `name`/`ruleId`
  with `InvalidParams`), hand the transform to the manager through the
  existing one-method `IConfigUpdater` seam, and reply with the
  refreshed snapshot via `ToWireFormat`. The `{ revision, changes }`
  envelope and the `Config.Subscribe` fan-out stay deferred to row 9.
- **Row 9 — `Config.Subscribe` events stream with
  snapshot-on-subscribe.** Additive on top of the row 1-2 prelude,
  mirroring `Logs.TailEngine`'s broadcaster. A singleton
  `SnapshotBroadcaster<JsonConfigSnapshot>` (the shared
  `Infrastructure/Events/` snapshot-on-subscribe core) caches the latest
  `JsonConfigSnapshot` and replays it as the first frame to every new
  subscriber (snapshot-on-subscribe — keyed state, not a pure live
  tail, so a late subscriber never needs a separate `Config.Get`).
  Each subscriber owns a bounded `Channel` (capacity 64); a
  sustainedly slow subscriber is dropped with a terminal
  `JsonConfigDroppedFrame` (reason `slow-subscriber`) while the
  remaining subscribers keep flowing, and graceful `Complete` closes
  every channel with a clean EOF (no terminal frame). Frames travel
  as a discriminated `JsonConfigStreamFrame` envelope
  (`kind: "snapshot" | "dropped"`). `ConfigFileService` bridges the
  manager's change event into `TryPublish` and primes the cache once
  at startup with the disk-loaded snapshot (the initial load raises no
  change event). `DispatchPolicy` routes `Config.Subscribe` to a
  streaming handler whose subscription disposal is handed off to
  `StreamingHandlerResult.PostFlush`, so the processor's `finally`
  releases the broadcaster slot on peer-close, cancellation, or
  iterator fault. The `{ revision, changes }` envelope and
  batch-coalescing fan-out stay deferred.

**Prelude — server-streaming responses on the `rpc` pipe.** Phase 3
is the first phase that needs `*.Subscribe` semantics
(`Config.Subscribe`), and Phase 2 row 6 deferred `Logs.TailEngine`
for the same reason. Before Phase 3's first row lands, two
additional commits ship on a Phase 3 prelude branch:

1. `feat(engine-core): support rpc server-streaming` — **DONE**.
   Introduces the discriminated `JsonRpcStreamFrame` envelope
   (`kind: "next" | "complete" | "error"`, request id echoed on
   every frame as a future-multiplex correlator) and splits
   `RpcHandlerResult` into two sealed records: `UnaryHandlerResult`
   (the existing single-`JsonRpcResponse` shape) and
   `StreamingHandlerResult` (an `IAsyncEnumerable<JsonElement>` of
   payloads, terminated by exactly one synthesised
   `JsonRpcStreamComplete` or `JsonRpcStreamError` frame).
   `RpcConnectionProcessor` type-switches on the result; streaming
   is always terminal (one stream per connection); cancellation /
   peer-close exit without a terminal frame; iterator faults
   surface as a structured `JsonRpcStreamError` (generic message
   on the wire, full exception logged). `PostFlush` runs in a
   `finally` for streaming so handler-supplied cleanup
   (subscription disposal) cannot leak even when the peer hangs
   up mid-stream.
2. `feat(engine): serve Logs.TailEngine over rpc` — **DONE**.
   The deferred half of Phase 2 row 6, now trivial on top of (1):
   `DispatchPolicy` consumes the logs
   `Broadcaster<JsonLogRecord>.Subscribe()` and maps it through
   `LogFrameStream` to yield each
   `JsonLogStreamFrame` (record/dropped) as a `JsonElement`;
   subscription disposal is handed off to
   `StreamingHandlerResult.PostFlush` so the processor's `finally`
   guarantees the broadcaster slot is released even on
   peer-close, cancellation, or iterator fault.

With those landed, `Config.Subscribe` and every later `*.Subscribe`
row becomes a small additive change.

**Goal**: engine owns `.autocontext.json`. Reads are concurrent and
lock-free; writes are single-writer with debounce + batch
coalescing; cross-instance writes mediate through `FileShare.None` +
the FS-watcher path; subscribers see one batch envelope per coalesced
write.

**Design anchors**: `§ RPC surface` (`Config.*`),
`§ Reload coalescing: debounce and batch`,
`§ Process scoping` (cross-instance rules), `§ P9`,
`§ Cross-instance .autocontext.json writes race on disk` pitfall.

**Code touch**:
- `AutoContext.Engine.Core/Workspace/Config/ConfigFileManager` —
  port of today's `AutoContextConfigManager` (TS) into .NET.
  `.autocontext.json` keys are camelCase only (`engine`,
  `instructions`, `mcpTools`, `disabled`, `disabledRules`,
  `version`) — no dual-casing, no key normalisation. (The
  on-disk shape was finalised after this phase by the
  `fix(config): correct .autocontext.json on-disk format` commit,
  which renamed the whole-item toggle to `disabled: true`, renamed the
  per-file rule opt-out list to `disabledRules`, dropped the
  bare-`false` `mcpTools` shorthand so every entry is an object, and
  added the `engine` block — see the three-participant model below.)
  The manager owns
  the live snapshot and exposes `LoadAsync` / `RefreshAsync` /
  `UpdateAsync` / `Watch` with a `Changed` event. It implements
  `IConfigSnapshotAccessor` (the lock-free `Current` read seam) and
  `IConfigUpdater` (the write seam); a `ConfigFileService` hosted
  service performs the initial disk load and arms the watcher at
  engine start so the snapshot is populated before the first
  `Config.Get` can land.
- Three-participant config model split out from the manager: an
  immutable **domain graph** (`ConfigSnapshot` + `ConfigEngineSettings`
  + `ConfigDiagnostic` + `ConfigInstructionsFile` + `ConfigMcpTool`,
  pure data, no behaviour) that the rest of the engine reads, an
  **on-disk wire DTO** layer (`JsonConfigFile` + `JsonConfigFileEngine`
  + `JsonConfigFile*` records, every `mcpTools` value an object entry
  — no bare-`false` shorthand) that mirrors the file shape
  byte-for-byte, and
  the **`Config.*` RPC wire DTOs** (`JsonConfig*` under
  `Engine.Protocol/Messages/Config`) the engine streams to clients.
  `ConfigSnapshotExtensions.ToFileFormat` (domain -> on-disk) and
  `JsonConfigFileExtensions.ToDomainGraph` (on-disk -> domain) are the
  crossing points for persistence, while
  `ConfigSnapshotExtensions.ToWireFormat` (domain -> RPC) projects the
  snapshot onto the transport; the domain graph never leaks either
  wire shape. `ConfigFileFormat` owns the JSON options, key order,
  and parse normalisation.
- `FileSystemWatcher` + per-resource trailing-edge debounce
  (~75–150 ms, `EngineOptions` constant). Reads on timer fire only,
  never inside the watcher callback. Cancellation propagates through
  the engine's root token (P8). **Status:** the watcher itself landed
  in row 3 (`Watch` starts it; `ReconcileFromWatcherAsync` calls
  `RefreshAsync`), but it fires `RefreshAsync` directly on every FS
  event — the trailing-edge debounce (and its `EngineOptions`
  constant) is still row 4. Until it lands, an atomic-rename or
  truncating-save burst triggers one read per raw FS event; the
  signature suppressor keeps each redundant read from fanning out,
  but the reads themselves are not yet coalesced.
- Deep-equal short-circuit (self-write suppressor): post-debounce
  parse compared by content hash against the current snapshot's
  source hash; equality skips the swap, the fan-out, and the
  revision bump. **Status:** landed in row 3 as a signature
  comparison — `PersistAsync` records the SHA-256 of the bytes it
  wrote in `_lastSignature`, and `RefreshAsync` skips the swap and
  fan-out when the freshly-read file hashes to the same value. This
  is the self-write suppressor; the only piece still owed is the
  revision-bump skip, which arrives with the revision counter (rows
  8–9).
- Writer mutex (`SemaphoreSlim`, P9). Writer-side micro-batch window
  (~5–10 ms) folds queued `Config.Toggle*` calls into one
  on-disk write, one snapshot swap, one fan-out envelope of shape
  `{ revision, changes: [...] }`. **Status:** landed in row 6 — the
  `_gate` semaphore serialises every `UpdateAsync` against watcher
  reconciliations, and `ConfigBatchWriter` (reached through
  `UpdateBatchAsync`) folds a burst of enqueued edits into a single
  `UpdateAsync` (one write, one swap, one `Changed` fan-out). The
  `{ revision, changes }` envelope shape still waits on the revision
  counter (rows 8–9).
- `Config.Get`, `Config.Subscribe`, `Config.ToggleFile`,
  `Config.ToggleRule` handlers. **Status:** `Config.Get` landed in
  row 7 (`DispatchPolicy.HandleConfigGet` projects the accessor's
  current snapshot through `ToWireFormat`); the `Config.Toggle*`
  handlers landed in row 8 (`DispatchPolicy` routes both to unary
  handlers that hand a pure `ConfigToggle` transform to the manager
  via `IConfigUpdater` and reply with the refreshed snapshot);
  `Config.Subscribe` is still row 9.
- Snapshot-on-subscribe (P6) — every new subscriber receives the
  current state as the first frame.

**Cohesion with the row-3 manager (open decisions for rows 8–10).**
The toggle round-trip is: a client (extension tree view, hook)
calls `Config.ToggleFile` / `Config.ToggleRule` on the `rpc` pipe →
the handler calls `ConfigFileManager.UpdateAsync(edit)` with
a pure `with`-expression that flips the `Disabled` flag on the
matching `ConfigInstructionsFile` / `InstructionsRule` /
`ConfigMcpTool` / `McpTask` → `UpdateAsync` persists (recording the
write signature), swaps the snapshot, and raises `Changed` → the
`SnapshotBroadcaster<JsonConfigSnapshot>` fans the new snapshot out to
`Config.Subscribe` subscribers. The local writer's own subsequent
FS-watcher event is then suppressed by the signature match, so the
toggle fans out exactly once; peer engines pick up the disk write
through their own watcher → `RefreshAsync` → `Changed`. The
immutable domain graph makes the `edit` delegate trivial, and the
gate already serialises edits against watcher reconciliations, so
the *single-engine* flow is sound today. The writer-side batch
coalescing seam landed in row 6 (`ConfigBatchWriter`); four seams
still have to be resolved as rows 8–10 land, and the manager's
current shape constrains how:
  - **Revision counter.** `Changed` is `EventHandler<ConfigSnapshot>`
    and carries no revision. The fan-out envelope is
    `{ revision, changes: [...] }`, so the revision must be assigned
    when the snapshot is published. Cleanest is to mint it inside
    `UpdateAsync` / `RefreshAsync` under the gate (monotonic `long`,
    per-instance) and widen `Changed` to carry `(snapshot,
    revision)` rather than have the broadcaster invent one outside
    the lock and risk reordering.
  - **`changes` delta.** The broadcaster needs the *previous*
    snapshot to compute the `changes` list, but `Changed` exposes
    only the new one. Either widen the event to `(previous, next,
    revision)` or have the toggle handlers emit their own change
    descriptors; decide before row 8/9 so the delta is computed
    under the gate, not reconstructed afterwards.
  - **No-op detection granularity.** `UpdateAsync` skips the write
    only on `ReferenceEquals(edited, current)`, i.e. the handler
    must return the *same* instance for a true no-op. A toggle that
    rebuilds an equal-but-not-same graph (e.g. setting a flag to the
    value it already holds) would still write and fan out. Since the
    domain records have value equality, switching the guard to `==`
    would absorb these redundant toggles cheaply — worth doing when
    the toggle handlers land.
  - **`FileShare` mode.** `PersistAsync` writes with
    `FileShare.Read`; *Process scoping* mandates `FileShare.None` +
    exponential-backoff retry so two engines can't interleave a
    read-modify-write. The single-engine path is unaffected, but the
    write-share mode and retry must tighten when cross-instance
    coordination (row 10) is exercised.

**Tests**:
- Atomic-rename burst on `.autocontext.json` (Windows + WSL shapes)
  coalesces to one reload + one fan-out.
- In-place truncating-save burst also coalesces to one fan-out.
- Local writer-side batch: three back-to-back
  `Config.Toggle*` calls produce one batch envelope with
  `changes.length == 3` in writer-mutex order; revision increments
  once.
- Self-write suppressor: local toggle produces exactly one fan-out
  (writer's), not two (writer + watcher echo).
- Peer-write reload (a second process writes the file): the engine
  reloads once and emits one batch envelope.
- Snapshot immutability: a reader holding a snapshot reference
  observes no mid-flight mutations across a concurrent reload.
- `Config.Subscribe` cold-start frame contains the current state;
  late subscribers don't need a separate `Get`.

**Out of scope**: `Instructions.*` consumers of the disabled state
(Phase 6 — config changes here will fan out to instructions
projection there).

## Phase 4 — Workspace detection

**Status**: Completed on branch `features/workspace-detection`.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(protocol): add Workspace.Detect and Info wire DTOs` | DONE |
| 2 | `feat(engine-core): add workspace detection rule tables` | DONE |
| 3 | `refactor(engine-core): model rules with file selectors and content scans` | DONE |
| 4 | `feat(engine-core): add WorkspaceContextDetector` | DONE |
| 4b | `refactor(engine-core): walk workspace via FileSystemEnumerator` | DONE |
| 4c | `refactor(engine-core): extract file classifier and contribution index` | DONE |
| 5 | `feat(engine-core): derive extensions index from file rules` | DONE |
| 6 | `feat(engine): serve Workspace.Detect over rpc` | DONE |
| 7 | `feat(engine): serve Workspace.Info over rpc` | DONE |
| 8 | `test(engine): cover per-flag detection and activation cascade` | DONE |
| 9 | `test(engine-core): smoke incremental watch detection` | DONE |
| 9b | `test(engine): cover Workspace.Detect and Info over rpc end-to-end` | DONE |
| 10 | `docs(plan): mark Phase 4 complete` | DONE |

**Goal**: engine runs `Workspace.Detect` on startup against its
own `--workspace` path, exposes the result via `Workspace.Detect` and
`Workspace.Info`, and produces the `extensions[]` index the coarse
`applyTo` filter consumes in Phase 6.

**Design anchors**: `§ RPC surface` (`Workspace.*`),
`§ P7` (coarse/fine match split), the ~60-flag table in
`§ RPC surface` *`Detect` return shape*.

**Code touch**:
- **Rule tables (rows 2–3).** The detection rules land as three
  `static readonly` lists of plain records — `FilePresenceRule`,
  `ContentScan`, `FlagActivationEdge` — registered in DI as the
  corresponding `IReadOnlyList<T>` singletons. No `I*Rules`
  interfaces, no provider types: the detection probes are different
  *operations* (filesystem traversal for presence, manifest-body
  regex for content scans) and the activation graph is a third
  concept entirely (graph closure, no FS, no file content) —
  collapsing them under one `I*Rules` interface would be
  shape-driven naming, not concept-driven naming. Per-flag test
  fixtures compose the detector with trimmed lists; the substitution
  surface is *data*, not *behaviour*, which is what `IReadOnlyList<T>`
  already gives us — the "no interface without a second impl"
  invariant therefore never fires here. Same flag names, same regex
  patterns, same `[child, parent]` activation edges as the TS port;
  no rule expansion, the existing ~60-flag set is the contract.
- **Content detection is grouped by manifest, not split by platform.**
  The TS port carries two parallel `npmContentRules` /
  `dotnetContentRules` arrays whose elements are byte-for-byte the
  same shape (`{ flag, pattern }`), differing only in which manifest
  the patterns scan. Modelling that as two identical record types
  (`NpmContentRule`, `DotNetContentRule`) would mean a third platform
  is a third identical type. Instead a single `ContentScan` groups a
  set of file selectors with the `ContentPatternRule` probes run
  against those files' bodies, so a new platform is a *data* edit —
  one more `ContentScan` row — not a new type:

  ```csharp
  internal sealed record FileSelector(string Value, FileSelectorKind Kind);

  internal sealed record ContentScan(
      IReadOnlyList<FileSelector> Selectors,
      IReadOnlyList<ContentPatternRule> Rules);

  internal sealed record ContentPatternRule(string Flag, Regex Pattern);

  internal enum FileSelectorKind { Extension, FileName, GlobPattern }
  ```

  The `ContentScans` table holds two entries: the npm scan
  (`package.json` by `FileName`, 12 patterns, case-sensitive except
  `hasGraphql`) and the .NET scan (`csproj`/`fsproj`/`vbproj` by
  `Extension`, 20 patterns, all case-insensitive). Case sensitivity
  lives inside each `Regex`, never at the type level. Grouping the
  selectors with their rules mirrors the detector's
  read-each-manifest-once loop, so the table shape already matches
  how the scan executes. DI collapses from four singletons to three.
- **File rules are *not* a 1:1 port of the TS glob strings.** Row 2
  shipped `FilePresenceRule(Flag, IReadOnlyList<string> Globs)` as
  a faithful port; row 3 reshapes it into a typed-selector model so
  the detector never has to re-parse glob *strings* the way the TS
  reverse maps do (`/^\*\*\/\*\.\{([^}]+)\}$/`):

  ```csharp
  internal sealed record FilePresenceRule(
      string Flag,
      IReadOnlyList<FileSelector> Selectors);
  ```

  A rule matches if **any** selector matches (pure OR); each selector
  is one criterion. `FileSelector` is a named record (not a bare
  tuple) because it is reused at two call sites — `FilePresenceRule`
  *and* `ContentScan` — and `FileSelectorKind` names the shared
  selection vocabulary; the `(Value, Kind)` pair is deliberate, with
  no nullable-triple invariant and no speculative modifier
  properties. `FilePresenceRule` keeps the "presence only — never
  reads the body" precision in its name (the body scan is
  `ContentScan`'s job). Of the 32 file rules, most are `Extension`
  selectors, nine add a `FileName` selector (`Cargo.toml`,
  `go.mod`, `pom.xml`, …), and exactly two need a real
  `GlobPattern` (`**/Dockerfile*`, the Unity
  `ProjectSettings.asset` path). The selector set is the single
  source of truth from which the detector derives both its
  classification dictionaries and its watch filter.
- **`WorkspaceContextDetector` (row 4) — single-pass, inverted
  index.** The TS detector issues ~40 separate `vscode.findFiles`
  globs plus a parallel hand-maintained watch-glob list; the engine
  has no indexed `findFiles`, so a verbatim port would mean ~40
  tree walks. Instead, an index built once from the selectors —
  `byExtension`, `byFileName`, and the small glob list — lets a
  **single** workspace traversal classify every file by lookup.
  Incremental updates use an inverted index rather than the TS
  re-glob-on-delete scheme:
  - `_contributions`: path → the set of base flags that file
    currently contributes.
  - `_baseCounts`: base flag → live contributor count; a base flag
    is on iff its count `> 0`.

  Each watcher event reclassifies **one** path (no FS walk, no
  glob), diffs its old vs new contribution set, adjusts the counts,
  and re-runs only the cheap activation cascade. Deletes are O(1)
  — "is there another file satisfying this flag?" is a count check,
  not a re-glob — and a content change rereads exactly the one
  changed manifest. Cold start seeds the same index in the single
  opening pass, so one mechanism serves both cold start and warm
  updates. The recursive watch is a raw `FileSystemWatcher`
  (`IncludeSubdirectories = true`) whose in-code filter is derived
  from the selector dictionaries, with `node_modules` / `bin` /
  `obj` / `.git` excluded. **This derivation closes a latent TS
  drift bug**: `hasYaml`, `hasDart`, `hasRuby`, and `hasSwift` are
  in `fileRules` but their extensions are absent from
  `existenceWatchGlob`, so today they are detected on a full scan
  yet never update incrementally. Deriving the watch set from the
  rules makes that class of drift unrepresentable. Synthetic flags
  (`hasGit`, `hasNodeJs`) and the cascade-to-fixpoint semantics are
  preserved exactly.
- **Workspace walk via `FileSystemEnumerator` (row 4b).** Row 4
  shipped the single opening pass as a hand-rolled `Stack<string>`
  walk that opened each directory twice — once via
  `Directory.EnumerateDirectories` to find subdirectories to prune
  and recurse, once via `Directory.EnumerateFiles` to yield files —
  with a `SafeEnumerate` wrapper that drove the enumerator manually
  so a mid-walk `IOException` could be swallowed (the BCL's lazy
  `Enumerate*` can't `yield` inside a `try`/`catch`, and an
  `EnumerationOptions { RecurseSubdirectories = true }` walk can't
  prune a subtree). Row 4b replaces that with a single
  `FileSystemEnumerator<string>` subclass: one directory open per
  directory (files and subdirectories reported from the same native
  scan), `ShouldRecurseIntoEntry` prunes `node_modules` / `bin` /
  `obj` / `.git` against the zero-allocation
  `ReadOnlySpan<char>` `FileSystemEntry.FileName` (no per-entry path
  string, no `Path.GetFileName`), and `ContinueOnError` skips the
  single offending entry rather than aborting the rest of a
  directory the way `SafeEnumerate`'s `yield break` did — the
  resilience that matters for a detector pointed at a workspace
  being actively edited. The lambda-driven `FileSystemEnumerable<T>`
  was considered and rejected for this row precisely because it has
  no `ContinueOnError` seam: it relies on `IgnoreInaccessible` for
  the common case and propagates any other mid-walk fault, which is
  weaker than the per-entry skip the subclass gives. `IsExcluded`
  (the watcher-event relative-path filter) is a separate code path
  and is unaffected.
- **Derived `extensions[]` (row 5).** A plain record produced by
  one `Detect` call — owned by the result, not DI-registered (no
  shared lifetime to manage). Built from the union of the
  `Extension`-kind selectors of every active file-rule flag, so a
  new file-rule flag automatically extends the extension set;
  content-scan flags contribute none.
- **`Workspace.Detect` and `Workspace.Info` handlers (rows 6–7).**
  The detector has **no** business with `.github/instructions/`
  content — that inventory is owned by
  `Instructions/InstructionsOverridesWatcher` (Phase 6) and
  reachable via `Instructions.List`. The TS reference port
  (`src/AutoContext.VsCode/src/workspace-context-detector.ts`)
  already enforces this split: it does not scan overrides;
  `instructions-files-override-watcher.ts` does. The .NET port
  mirrors that separation of concerns.

**Tests**:
- One fixture-per-flag test asserting each rule fires only on its
  declared trigger.
- Activation cascade: `hasNextJs` triggers `hasReact` triggers
  `hasNodeJs` without re-running the file scans.
- Incremental updates via the inverted index: creating a file flips
  its flag on; deleting the *last* contributor flips it off while a
  surviving sibling keeps it on (count-based, no re-glob); a
  manifest content change reclassifies only that file.
- Watch-filter derivation regression: every file-rule extension is
  present in the derived watch set (guards against the TS
  yaml/dart/ruby/swift drift).
- `extensions[]` derivation matches the union of every active
  file-rule flag's `Extension` selectors; content-scan flags
  contribute none.
- `Workspace.Info` returns engine-process metadata distinct from
  `Detect`.
- `Workspace.Detect` return shape carries **no** `overrides` field
  (negative-shape test against the wire contract): a workspace with
  files under `.github/instructions/` produces the same `Detect`
  envelope as a workspace without — the detector is blind to
  override content.
- End-to-end over rpc (row 9b, integration suite): spawning the
  `autocontext-engine` binary against a populated `--workspace` and
  calling `Workspace.Detect` / `Workspace.Info` over the `rpc` pipe
  returns the startup-scan flag set, the derived `extensions[]`, the
  engine version, and the spawned instance identity — the same
  contract the in-process handler tests assert, now exercised across
  a real process boundary and serialised wire frames (including the
  negative `overrides`-shape check). Gated `Category=Smoke`.

**Out of scope**: `Discovery.RouteForPrompt` extension index (Phase 9
— consumes the same data but lives in its own service).

## Phase 5 — Instructions corpus build-time pipeline

**Status**: Completed on branch `features/instructions-corpus-build-time-pipeline`.
The MCP-tools registry and its sibling hand-authored UI catalog
(`mcp-tools-catalog.json`) moved to Phase 7, where the engine first
owns the registry (see *Scope note* below).

> **Superseded by the catalog+manifest redesign.** This phase shipped
> two generated files — `instructions-files.json` ("wire shape") and
> `instructions-files-metadata.json` ("internal") — under the original
> *wire ≠ internal split* reading of P3. That reading fused three
> independent layers and silently dropped the curatorial taxonomy
> (categories / `label` / `activationFlags`). The corrected design
> (see `§ P3 — three decoupled representations` and `§ Resource
> manifests`) replaces those two files with a **hand-authored**
> `instructions-catalog.json` (curatorial layer, tracked in source)
> and a **build-generated** `instructions-manifest.json` (per-file
> facts). The commit ladder below is preserved as historical record;
> the redesign is tracked on
> `features/fix-metadata-vs-manifest-design`. Read the Phase 5 prose
> that follows as the *original* plan — the catalog+manifest model
> overrides it wherever they disagree.

| # | Commit subject | State |
|---|---|---|
| 1 | `refactor(engine): copy instruction corpus into engine host` | DONE |
| 2 | `feat(build-tasks): scaffold build task projects` | DONE |
| 3 | `feat(build-tasks): generate instructions manifest` | DONE |
| 4 | `refactor(instructions): add shared parser project` | DONE |
| 5 | `refactor(instructions): replace MSBuild task with console app generator` | DONE |
| 6 | `feat(instructions-manifest-gen): emit instructions-files-metadata.json` | DONE |
| 7 | `test(instructions): cover corpus round-trips and applyTo parser` | DONE |
| 8 | `docs(plan): mark Phase 5 complete` | DONE |

**Goal**: a single build-time pass over `src/AutoContext.Engine/Instructions/`
produces both `Resources/instructions-files.json` (wire shape) and
`Resources/instructions-files-metadata.json` (engine-internal
indices). The `applyTo` parser ships in `AutoContext.Instructions.Parser`,
parses only, and is round-trip-verified per fixture.

**Design anchors**: `§ Resource manifests`,
`§ applyTo` matching subsection under `Instructions.*`,
`§ P3` (three decoupled representations), `§ applyTo parser pitfall`.

**Architecture note (as-built)**: the original plan put the builder in
a `netstandard2.0` MSBuild `ITask` library (`AutoContext.Build.Tasks`).
That was replaced by two `net10.0` projects: a shared parser library,
`AutoContext.Instructions.Parser` (frontmatter + `applyTo` parsing —
since extended with a body section index and pure cross-file
`[locator#fragment]` reference resolution; referenced by both the
build-time generator and the engine runtime so one source is compiled
for both), and a console generator,
`AutoContext.Instructions.Manifest.Generator` (AssemblyName
`instructions-manifest-gen`). The engine invokes the generator with
`<Exec>` from `InstructionsManifestGenerator.targets` rather than
loading an `ITask` into MSBuild — this sidesteps the
Full-Framework/Core MSBuild load constraint and lets the generator
target `net10.0` like the rest of the engine. Sequencing is a
`ProjectReference` from `AutoContext.Engine.csproj`
(`ReferenceOutputAssembly=false`); the target runs
`AfterTargets="ResolveProjectReferences" BeforeTargets="CoreCompile"`.

**Scope note (MCP-tools registry moved to Phase 7)**: an earlier
draft folded the MCP-tools registry rename
(`mcp-workers-registry.json` → `mcp-tools-registry.json`) and a
build-time `mcp-tools.json` projection into this phase. That work
moved to Phase 7 — the phase where the engine first *owns* the
registry (`McpTools.List`/`Invoke`). There is no rename: today's
`src/AutoContext.Mcp.Server/mcp-workers-registry.json` stays in
place under its legacy name, serving the still-live MCP server until
Phase 15 deletes that project wholesale. The engine authors its own
`Resources/mcp-tools-registry.json` (and schema, and the
hand-authored `mcp-tools-catalog.json`) **fresh, correctly named**
in Phase 7 — the same copy-into-the-engine pattern this phase uses
for the instruction corpus, where the old consumer keeps working
untouched and the new file is born named for the project that owns
it. (The earlier "build-time `mcp-tools.json` projection" framing is
superseded — both the registry and the catalog are now
hand-authored, like the `instructions-catalog.json` /
`instructions-manifest.json` split; see the *New direction* note
under Phase 7.)

**Code touch**:
- **Create `AutoContext.Instructions.Parser/` and
  `AutoContext.Instructions.Manifest.Generator/`** — two new `net10.0`
  projects (shared parser library + console generator), each with a
  sibling test project (`AutoContext.Instructions.Parser.Tests`,
  `AutoContext.Instructions.Manifest.Generator.Tests`). Added to
  `AutoContext.slnx` and `build.ps1` in the same change. The
  implementations described below land in these projects as they are
  introduced.
- Curated instruction corpus is **copied** to
  `src/AutoContext.Engine/Instructions/` — the binary host owns the
  side-cars (P5). Today the corpus is co-located with the VS Code
  extension at `src/AutoContext.VsCode/instructions/`; the engine
  binary becomes the owner and the files ship next to the binary
  (resolved at runtime via `AppContext.BaseDirectory`, not embedded
  resources). This is a **copy, not a move**: the VS Code copy stays
  in place and untouched so the extension's existing TS generators,
  `package.json` `chatInstructions` wiring, and packaging keep
  working unchanged. The VsCode original is deleted (and its
  generators/packaging repointed at the engine corpus) in the later
  phase that connects the engine to the VS Code extension — not
  here. The `Instructions/` side-car folder under
  `src/AutoContext.Engine/` is created here (populated with the
  copied corpus); the `Resources/` folder is created in row 4 when
  the build task first writes into it.
- `InstructionsListBuilder` + `InstructionsManifestSerializer` — the
  corpus scan, curatorial validation, and deterministic JSON writer
  live in the `AutoContext.Instructions.Manifest.Generator` console
  exe rather than the engine runtime library, so the build-time
  generator and round-trip verifier ship nothing at runtime. The
  `.targets` file is imported by `AutoContext.Engine.csproj` (binary
  host — the project that owns the output `Resources/` folder) and
  `<Exec>`s the generator, which writes `instructions-files.json`
  (and, in row 6, `instructions-files-metadata.json`) into
  `src/AutoContext.Engine/Resources/`. The frontmatter + `applyTo`
  parser lives in `AutoContext.Instructions.Parser` and is referenced
  by both the generator and the engine runtime via a normal
  `ProjectReference`, so build-time validation and runtime parsing
  compile one source. Today's
  `instructions-files-metadata-generator.ts` (TS) is retired; the
  .NET generator replaces it as the single producer.
- `applyTo` parser: comma-split, brace-expand `{a,b,c}` groups,
  trim whitespace, extract extension set. Round-trip invariant
  (`recomposed == original` modulo whitespace) checked per
  corpus file at build time; a failing round-trip fails the build.

**Tests**:
- Builder round-trips for every file in the corpus.
- Wire/internal split: `instructions-files.json` round-trips against
  the `Instructions.List` envelope (test asserts equality);
  `instructions-files-metadata.json` carries section maps and parsed
  `applyTo` extension sets and never leaks onto the wire.
- `applyTo` parser fixtures: comma lists, brace expansion, nested
  globs (`**/*.{cs,fs,vb}` → three globs), idempotence
  (parse∘compose ≈ identity).
- A `applyTo` value that would silently canonicalise (e.g. `**` vs.
  `**/*`) is preserved verbatim; the parser refuses to "simplify".

**Out of scope**: any runtime projection (Phase 6); the
full-text search index (Phase 6 builds it in-memory over the
projected bodies, not from any build-time seed).

## Phase 6R — Design remediation: catalog + manifest split

**Status**: Completed on branch `features/fix-metadata-vs-manifest-design`,
merged to `dev/autocontext-engine`. The runtime snapshot merge and the
`Instructions.Categories` DTO (see **Out of scope**) resume in Phase 6.

**Why this phase exists**: Phase 6 runtime work was started and then
**stashed** mid-flight when a critical design flaw surfaced. Phase 5's
two generated files — `instructions-files.json` ("wire shape") and
`instructions-files-metadata.json` ("internal") — had converged to
~80% duplicate content because the original P3 reading fused three
independent layers (on-disk format / runtime model / wire shape) and
silently dropped the curatorial taxonomy (categories / `label` /
`activationFlags`) the LM-tools surface still needs
(`list_autocontext_instructions_files` returns `categories`). Building
Phase 6 on top of that shape would have meant porting the flaw into
the runtime and **redoing the work** once it was caught downstream.
This phase corrects the design at the build-time source **before**
Phase 6 resumes. The two files are replaced per the corrected
`§ P3 — three decoupled representations`.

**Goal**: replace the two generated files with one **hand-authored**
curatorial layer (`instructions-catalog.json`, tracked in source) and
one **build-generated** per-file facts manifest
(`instructions-manifest.json`). The generator reads the catalog,
cross-validates it against the corpus, and emits only the manifest.
Phase 6 resumes on top of the corrected files.

**Design anchors**: `§ P3` (three decoupled representations),
`§ Resource manifests` (catalog hand-authored + manifest generated),
`§ RPC surface` (`Instructions.List` + `Instructions.Categories`).

**Code touch**:
- **Generator** (`AutoContext.Instructions.Manifest.Generator`):
  - `InstructionsManifestBuilder` emits `instructions-manifest.json`
    — per-file facts only (`key`, `fileName`, `name`, `version`,
    `description`, `applyTo?`, `extensions?`, `hasChangelog`,
    `contentHash`, `sections[]`). The old wire-shape
    `instructions-files.json` builder is dropped. The manifest carries
    no `categories`/`label`/`activationFlags` (the catalog's) and no
    workspace-state fields (`disabled`/`source` are per-request).
    `alwaysAttached` is **declared** in the catalog's `alwaysAttached`
    array (the single source of truth) and derived by the engine at
    merge time, not baked into the manifest.
  - `InstructionsCatalogReader` reads the hand-authored
    `instructions-catalog.json` and cross-validates against the
    corpus, all rules build-FATAL with an
    `[instructions-catalog.json] …` message: **(A)** every catalog
    entry resolves to a real corpus file; **(B2)** every corpus file
    has a catalog entry **except** the always-attached files (`copilot`,
    `autocontext` — matching the legacy TS exemption); **(membership)**
    every category-membership string resolves to a declared category.
  - Update `Program.cs` args (corpus dir + catalog path + manifest
    output path), `InstructionsManifestGenerator.targets` `<Exec>`,
    the DI registrations, serializers, and JSON source-gen contexts
    to the new shape.
- **Catalog authoring**: author
  `src/AutoContext.Engine/Resources/instructions-catalog.json` by
  porting the TS `src/AutoContext.VsCode/resources/instructions-files.json`
  (categories + `label` + membership + `activationFlags`). Remove it
  from `src/AutoContext.Engine/.gitignore` — the catalog is tracked
  source; only the generated `instructions-manifest.json` stays
  ignored — and wire its copy-to-output.

**Tests**:
- Catalog validation: rules **A** / **B2** / **membership** each fail
  the build with the `[instructions-catalog.json] …` message on a
  crafted bad fixture; the real catalog passes.
- Manifest shape: emitted `instructions-manifest.json` carries the
  per-file facts and **none** of the catalog-only fields; section maps
  and parsed `applyTo` extension sets round-trip.
- Deterministic, byte-stable manifest output (no spurious rewrites
  when inputs are unchanged).

**Out of scope** (these land as Phase 6 resumes, on top of the
corrected files):
- The runtime `InstructionsManifestSnapshot` merge + projection (the
  stashed Row 2 work, rebuilt to the new shape).
- The `Instructions.Categories` wire DTO in
  `AutoContext.Engine.Protocol` (depends on the Row 1 envelope work).

## Phase 6P — New span-based instructions parser

**Status**: Completed on branch `features/instructions-span-parser`,
merged to `features/instructions-corpus-runtime`. All rows landed: both
corpus consumers run on the new parser pair, and the legacy single-pass
parser has been deleted.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(instructions): add InstructionsFileSyntaxParser span model and enums` | DONE |
| 2 | `feat(instructions): implement InstructionsFileSyntaxParser block and token emission` | DONE |
| 3 | `feat(instructions): attach file-local diagnostics to spans` | DONE |
| 4 | `feat(instructions): add structured parser over the span stream` | DONE |
| 5 | `refactor(instructions-manifest-gen): repoint corpus parse onto the syntax parser` | DONE |
| 6 | `refactor(engine-core): repoint InstructionsFileService onto the syntax parser` | DONE |
| 7 | `refactor(instructions): delete the legacy regex InstructionsFileParser and make InstructionsFileFactory the sole structural entry point` | DONE |
| 8 | `docs(plan): mark Phase 6P complete` | DONE |

**Goal**: replace the single-pass regex parser with a
lower-level, incremental `InstructionsFileSyntaxParser` that emits
source-positioned `InstructionsFileSyntaxSpan`s, plus a structured layer
(`InstructionsFileFactory.FromFileAsync` for the disk entry point and
`Model.InstructionsFile.FromSpans` for the span→model rebuild) that
reconstructs the `InstructionsFile` model on top of the span stream. The
two current corpus consumers — the build-time
`AutoContext.Instructions.Manifest.Generator` and the runtime
`InstructionsFileService` — are repointed onto the new parser pair, and the
legacy regex parser and its static entry point are deleted.

**Design anchors**: the locked span-parser design contracts —
emit-level/emit-scope **intersection** model
(`ShouldEmit = MatchesLevel(kind) && MatchesScope(kind)`), gapless
non-overlapping `Blocks` partition, recursive
container-before-contained ordering, whole-file zero-based
exclusive-ended coordinates (`CRLF` = two chars, no normalisation),
span-attached file-local diagnostics with promotion to the nearest
emitted parent, the `## Rules` section state machine, and the
preserved fence asymmetry (rule bullets fence-agnostic; headings,
references, and the Rules-boundary `---` fence-aware).

**Code touch** (`AutoContext.Instructions.Parser/`):
- `InstructionsFileSyntaxParser` — `internal sealed`;
  `ParseFileAsync(string)` owns I/O (`FileStream` + `StreamReader`)
  and delegates to `ParseAsync(TextReader)`, which consumes decoded
  text incrementally and yields `InstructionsFileSyntaxSpan`. Ctor:
  `(emitLevel = Full, emitScope = All, includeDiagnostics = true)`.
- Span model, coordinate structs, and enums:
  `InstructionsFileSyntaxSpan` (`Text`, `Kind`, `TextSpan`,
  `LineSpan`, `Diagnostics`), `InstructionsFileTextSpan`,
  `InstructionsFileLineSpan`, `InstructionsFileSpanKind`,
  `[Flags] InstructionsFileSpanEmitLevel`,
  `[Flags] InstructionsFileSpanEmitScope`,
  `InstructionsFileDiagnostic`,
  `InstructionsFileDiagnosticKind` (`MissingTag`, `DuplicateTag`,
  `MalformedTag`, `MalformedReference`, `MisplacedRule`).
- Single streaming pass carries: an `inFence` toggle (gates
  headings/references and the `---` Rules boundary; rule bullets stay
  fence-agnostic), an "under `## Rules`" bool (enter on a `Heading2`
  whose trimmed text is exactly `Rules`, stay across `Heading3`, exit
  on the next `Heading2` / any `Heading1` / a thematic break `---` /
  EOF), and a seen-tag set for `DuplicateTag`. Diagnostics attach to
  the most specific emitted span, promoting to the nearest emitted
  parent when the specific span is filtered out by level/scope.
- Structured layer (`Model.InstructionsFile.FromSpans`, reached on
  disk through `InstructionsFileFactory.FromFileAsync`) — consumes the
  `Full`/`All` span stream and rebuilds the `InstructionsFile` model
  (frontmatter `name`/`description`/`applyTo`/`version`, the
  `##`/`###` section index with slug anchors, rule bullets,
  `[locator#fragment]` references split into Rule/Section kinds, and
  diagnostics carrying the span `InstructionsFileDiagnosticKind`
  directly with a body-relative line). `FrontmatterApplyToParser`
  glob parsing and `Slugify` are reused unchanged.
- The legacy single-pass regex parser, its static entry
  (`Parse` / `ParseAsync` / `TryParse*`), and
  `InstructionsFileTryResult` are deleted; `InstructionsFileFactory`
  (disk entry point) over `Model.InstructionsFile.FromSpans` is the
  sole structural entry point. (Post-ladder `refactor(parser): …`
  commits then split the syntax and model layers into `Syntax/` and
  `Model/`, collapsed the public surface, emitted span text as
  zero-copy memory slices, renamed the disk entry to
  `InstructionsFileFactory`, decoupled the parse into the four-stream
  `InstructionsFileSyntaxTree` and renamed the model rebuild
  `Model.InstructionsFile.FromSpans` → `Model.InstructionsFile.FromSyntaxTree`,
  and repointed the runtime body projector onto
  `InstructionsBodyProjector` — see the **Target structure** section
  for the current names.)
- Repoint `AutoContext.Instructions.Manifest.Generator/CorpusParser`
  and `AutoContext.Engine.Core/Features/Instructions/InstructionsFileService`
  onto `InstructionsFileFactory.FromFileAsync`. Both consumers adopt the
  async API directly — `CorpusParser.ParseAsync` and the already-async
  file service.

**Tests**:
- Span parser: emit-level × emit-scope truth table
  (`Full`/`Blocks`/`Tokens` × `All`/`Frontmatter`/`Headings`/`Rules`/`References`,
  including `Blocks + References` → empty); gapless `Blocks` partition
  over fixtures; recursive ordering
  (`FrontmatterProperty` → `FrontmatterKey`/`FrontmatterValue`;
  `TaggedRule` → `Tag`/`Reference`); whole-file coordinates with
  `CRLF` = two chars; diagnostic attachment + promotion across the
  three levels.
- Section state machine: `## Rules` enter / stay-across-`Heading3` /
  exit on each boundary kind; a `---` inside a fence does **not**
  close Rules; `MissingTag` only under Rules; `MisplacedRule` for a
  tagged rule outside Rules; a `PlainRule` outside Rules stays clean.
- Malformed-tag redefinition: `- [foo] **Do**` → `TaggedRule` +
  `Tag` + `MalformedTag` (captured as the one intentional parity diff
  versus the legacy parser).
- Structured-parser parity: across the shipped corpus the rebuilt
  `InstructionsFile` model matches the legacy parser
  field-for-field except the documented malformed-tag diff.
- Generator parity: `instructions-manifest.json` is byte-identical
  before and after the repoint.
- `InstructionsFileService` parity: projected bodies and
  disabled-rule filtering unchanged.

**Full retirement** (completed): both consumers and the test suite run on
the syntax parser + `InstructionsFileFactory` / `Model.InstructionsFile`;
the legacy regex implementation, its static entry, and
`InstructionsFileTryResult` are deleted.

**Out of scope**: zero-copy
`ReadOnlyMemory<char>` span slicing (deferred — accept per-span
`string` for now); first-class `CodeFence` / `CodeSpan` span kinds
(internal fence state only); corpus-level cross-file reference
resolution (stays in `InstructionsFileReferenceResolver`, outside the
span parser).

## Phase 6 — Instructions corpus runtime + projection

**Status**: Completed on branch `features/instructions-corpus-runtime`,
atop the merged **Phase 6P** (new span-based parser) and Phase 6R catalog
+ manifest shape. Every `Instructions.*` RPC is served from the in-memory
snapshot, with config-driven invalidation and content search.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(protocol): add Instructions.* wire DTOs` | DONE |
| 2 | `feat(engine-core): load instructions corpus snapshot on startup` | DONE |
| 3 | `feat(engine-core): add InstructionsOverridesWatcher with debounced reload` | DONE |
| 4 | `feat(engine): serve Instructions.List over rpc` | DONE |
| 5 | `feat(engine-core): add InstructionsFileService with override resolution and disabled-rule filter` | DONE |
| 6 | `feat(engine): serve Instructions.Get and GetAll over rpc` | DONE |
| 7 | `feat(engine): serve Instructions.GetAlwaysAttached over rpc` | DONE |
| 8 | `feat(engine): serve Instructions.GetRaw with bundled/override/active source` | DONE |
| 9 | `feat(engine-core): add InstructionsFullTextSearchService over projected bodies` | DONE |
| 10 | `feat(engine): serve Instructions.SearchContent over rpc` | DONE |
| 11 | `feat(engine-core): add Instructions.Subscribe events stream with snapshot-on-subscribe` | DONE |
| 12 | `feat(engine-core): rebroadcast Instructions.Subscribe on Config.Subscribe changes` | DONE |
| 13 | `feat(engine-core): warn when an override is older than its bundled file` | DONE |
| 14 | `test(engine): integration test for instructions projection and invalidation over rpc` | DONE |
| 15 | `docs(plan): mark Phase 6 complete` | DONE |

**Goal**: engine answers every `Instructions.*` RPC from in-memory
snapshots, applies per-request projection (disabled rules filtered,
`[INSTxxxx]` tags preserved so cross-rule references stay navigable,
overrides resolved), invalidates cleanly via
`Config.Subscribe`, and exposes content search.

**Design anchors**: `§ RPC surface` (`Instructions.*`),
`§ P2` (discriminated envelopes), `§ P3` (three decoupled
representations — disk catalog+manifest vs. runtime snapshot vs.
wire), `§ P9` (snapshot-immutable),
`§ alwaysAttached pitfall`, `§ Instructions.Get distinguishes disabled
from not-found pitfall`, `§ Override survival across upgrades`
pitfall.

**Code touch**:
- `AutoContext.Engine.Core/Features/Instructions/`:
  - `InstructionsManifestService` — on startup, merge the
    hand-authored `instructions-catalog.json` (categories, `label`,
    membership, `activationFlags`) with the build-generated
    `instructions-manifest.json` (per-file facts) into one immutable
    `InstructionsManifestSnapshot`; re-project per request.
  - `InstructionsBodyProjector` — projects a manifest entry's body on
    demand. `ToResponseBodyAsync` resolves override-vs-bundled, reads
    and parses the body, filters disabled rules, and slices the
    requested sections for `Get`; `ToSearchBodyAsync` rebuilds the
    offset-bearing body the search index consumes. `[INSTxxxx]` tags
    are **preserved** (not stripped): the id is the anchor a
    cross-rule / cross-file
    `[locator#fragment]` reference resolves to, so stripping them
    would leave every reference pointing at content the reader can no
    longer locate. Frontmatter is still stripped and disabled rules
    still filtered — only the tag-strip step is dropped relative to
    the original projector design.
  - `InstructionsFullTextSearchService` — in-memory full-text search
    over the **projected body** each file resolves to (the same text
    `Get` returns: override-over-bundled, frontmatter stripped,
    disabled rules filtered), plus the manifest `description`. The
    manifest carries no body text, so it supplies only the file roster
    and heading anchors; the searchable content comes from
    `InstructionsBodyProjector.ToSearchBodyAsync`. Built lazily, hot
    across queries,
    invalidated when an override changes (`InstructionsOverridesWatcher`)
    or disabled state changes (`Config.Subscribe`) — not on a corpus
    reload, since the bundled corpus is immutable at runtime.
  - `InstructionsOverridesWatcher` — a `FileSystemWatcher` per
    `engine.instructions.overridesRoots` root's `instructions/`
    subfolder (default `<workspace>/.github/instructions/`), with the
    same debounce shape Phase 3 introduced. Override resolution walks
    the roots in precedence order — the first root that supplies
    `<name>.instructions.md` wins, falling back to the bundled corpus.
- Handlers: `Instructions.List`, `Categories`, `Get`, `GetAll`,
  `GetAlwaysAttached`, `GetRaw` (with
  `opts.source: "bundled"|"override"|"active"`), `SearchContent`,
  `Subscribe`. Discriminated envelopes per `§ P2`. `List` rows carry
  `label` + category `membership` + computed `disabled`/`source`;
  `Categories` returns the taxonomy definitions (`[{name,
  description}]`) from the catalog, static/cached. `activationFlags`
  is engine-internal — it is evaluated against workspace state to
  derive `disabled` and is never serialised on the wire.
- `Config.Subscribe` consumer that re-evaluates `disabled` flags and
  rebroadcasts on `Instructions.Subscribe`.
- Override-mtime-vs-bundled-mtime warning (the *override survival*
  pitfall).

**Tests**:
- `List` returns every bundled + override file; disabled rows carry
  `disabled: true`; `alwaysAttached` flag correctly reflects YAML
  frontmatter; rows carry `label` + category `membership` from the
  catalog.
- `Categories` returns the catalog taxonomy definitions in
  deterministic order; every `membership` string on a `List` row
  resolves to a returned category.
- `Get` discriminated envelope: `ok` (with projected body),
  `disabled` (identity only — no description, no body, no version),
  `not-found` (just the name).
- `GetAll` filters disabled files unconditionally.
- `GetAlwaysAttached` returns only files with `alwaysAttached: true`
  in deterministic order; disabled always-attached files are
  omitted (`GetAlwaysAttached` never returns a `disabled`-envelope
  identity).
- `GetRaw` with each `source` value: `active` resolves
  override-over-bundled, `bundled` and `override` are explicit and
  preserve byte alignment with the source file (critical for the
  CodeLens use case).
- `SearchContent` returns scored hits with anchors; disabled files
  excluded by default; `opts.includeDisabled` flips them in.
- Snapshot immutability across a concurrent corpus reload: a reader
  iterating `GetAll` sees no torn state.
- `Config.ToggleFile` / `Config.ToggleRule` -> `Instructions.Subscribe`
  fan-out re-evaluates the `disabled` flag without a corpus reload.
- Override mtime older than bundled mtime emits a warning event.

**Out of scope**: LM-tool shims (Phase 14 — they dial these RPCs);
MCP-tool dispatch (Phase 7).

## Phase 7 — MCP tool catalog, dispatch, and worker manager

**Status**: Completed on branch `features/mcp-tools-catalog-and-dispatch`.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(protocol): add McpTools.* wire DTOs` | DONE (legacy task shape) |
| 1b | `refactor(protocol): flatten McpTools/config MCP DTOs` | DONE |
| 2 | `feat(engine): author mcp-tools-registry.json and its schema` | DONE |
| 3 | `feat(engine-core): add McpToolsRegistryLoader and schema validator` | DONE |
| 3b | `feat(engine-core): validate mcp-tools-catalog at loader startup` | DONE |
| 4 | `feat(build): generate workers.json from worker projects` | DONE |
| 5 | ~~`feat(build): project mcp-tools.json from the registry`~~ | REMOVED — catalog is hand-authored |
| 6 | `feat(engine-core): port WorkerManager with ensureRunning gate` | DONE (type since renamed to `WorkerProcessService`) |
| 6b | `refactor(engine-core): use pipe probe for readiness` | DONE |
| 7 | `feat(engine-core): serve McpTools.List over rpc` | DONE |
| 8 | `feat(engine-core): load workers.json and wire the worker manager` | DONE |
| 8b | `feat(engine-core): serve McpTools.Invoke over rpc` | DONE |
| 8c | `feat(engine-core): enrich McpTools.Invoke with editorconfig` | DONE |
| 9 | `test(engine): integration test for mcp tool dispatch over rpc` | DONE |
| 10 | `docs(plan): mark Phase 7 complete` | DONE |

**Goal**: engine absorbs today's `AutoContext.Mcp.Server` worker
dispatcher. `McpTools.List` and `McpTools.Invoke` answer over the
`rpc` pipe; the MCP-server-only role over stdio comes in Phase 11.
Workers are spawned by the engine via the same lazy
`ensureRunning(workerId)` pattern in use today. The engine also
becomes the owner of the MCP-tools registry, authoring
`mcp-tools-registry.json` (its schema, and the hand-authored
`mcp-tools-catalog.json` UI catalog and its schema) **fresh** under its own
`Resources/` rather than renaming today's `AutoContext.Mcp.Server`
copy.

**Design anchors**: `§ RPC surface` (`McpTools.*`), `§ Resource
manifests` (`workers.json`, `mcp-tools-registry.json`),
`§ McpTools.Invoke and MCP tools/call share one handler` pitfall,
`§ What the engine absorbs from today's topology`.

> **New direction (supersedes the "projection" framing in rows 2 / 5
> below).** The execution registry and the UI catalog are **both
> hand-authored**, following the same curatorial concept as the
> `instructions-catalog.json` /
> `instructions-manifest.json` split — there is **no build-time
> projection step**. The two files have a clean division of labour:
> `mcp-tools-registry.json` describes **what** each tool is for the
> model and how it dispatches, while `mcp-tools-catalog.json` answers
> **when** each tool activates and **where** it sits in the UI —
> neither restates the other's concern.
> `mcp-tools-registry.json` is a **flat `tools[]`**
> list (no nested worker → tool → task tree): each tool carries
> `name`, `workerId` (FK to `workers.json`), `description`,
> `parameters`, and an optional `editorconfig` array — the
> `description` and `parameters` being the model-facing contract
> surfaced over MCP `tools/list`. "Tasks" no
> longer exist on the wire or in the registry — each former task is
> its own top-level tool; tasks survive only as worker-internal
> checker classes, and a tool that bundles several checks (e.g.
> `analyze_csharp_code_style`) runs them behind one tool name. The
> sibling catalog is hand-authored as `mcp-tools-catalog.json`
> (renamed from the planned `mcp-tools.json`; same curatorial concept
> as `instructions-catalog.json`, its own shape): a hierarchical
> category tree whose `activationFlags` (accumulated down the tree and
> ANDed) gate **when** a tool is offered and whose category placement
> decides **where** it renders, carrying no model-facing contract of
> its own. It joins the
> registry by tool `name` +
> `workerId`. The engine merges registry + catalog at runtime for
> `McpTools.List`, analogous to how it merges the instructions catalog +
> manifest. Consequently the row-5
> `AutoContext.McpTools.Manifest.Generator` (`mcp-tools-manifest-gen`)
> projector has no projection job and has now been **unwired from the
> engine build** — the `ProjectReference`, the
> `McpToolsManifestGenerator.targets` import, the gitignored
> `Resources/mcp-tools.json` output, and the `.gitignore` entry are all
> removed, and the generator project and its tests have been **deleted
> from the tree**. Disable granularity
> correspondingly collapses from per-task to per-tool; the engine-side
> config model and wire DTOs are flattened to match.

**Landed-row notes.**

- **Row 6b — `refactor(engine-core): use pipe probe for readiness`.** The
  ported `WorkerManager` (row 6) gated readiness on scraping a worker's
  **stderr ready marker** (`WorkerProcessInfo.ReadyMarker`), inherited
  verbatim from the MCP-server port. That barrier is wrong for the
  engine: the engine owns the worker's named pipe, so the authoritative
  "ready" signal is *the pipe becoming connectable*, not a log line the
  worker happens to print. Row 6b replaces the marker with a connection
  probe:
  - `WorkerProcessInfo.ReadyMarker` → `Endpoint`; readiness is now the
    first successful dial of the worker's listen pipe via a new
    `IWorkerConnectionProbe` (production `WorkerConnectionProbe` retries
    the connect until it succeeds, the caller cancels, or the process
    exits). Stderr lines are now logged for diagnostics only.
  - The lifecycle was also restructured to remove a design smell: each
    worker now owns a `WorkerProcessHost` (the gate + current-instance
    identity) holding one `WorkerProcessInstance` per concrete spawn,
    and the instance *is* the `IProcessObserver`. Staleness is reference
    identity under the host gate (`TryAdopt` / `TryRetire` /
    `TryDetachProbe` / `IsCurrent`), replacing the earlier
    `instance.Slot.CurrentInstance` reach-around; the launch runs
    outside the gate so start-up stderr/exit callbacks cannot re-enter
    it. The public `EnsureRunningAsync` surface is unchanged.

**Code touch**:
- `AutoContext.Engine.Core/Workers/WorkerProcessService` (ported as
  `WorkerManager`, since renamed) — port of today's `WorkerManager`
  from `AutoContext.Mcp.Server/Workers/` into the engine library.
  `EnsureRunningAsync(workerId)` gate unchanged.
- `Resources/workers.json` build generator — aggregates the
  per-worker `.autocontext-worker.json` descriptors under
  `src/AutoContext.Worker.*/` ({ `id`, `type`, `command` },
  optional `label`) verbatim. A missing descriptor or an
  id-collision fails the build.
- **Author the MCP-tools registry fresh under the engine.**
  `Resources/mcp-tools-registry.json` and its
  `mcp-tools-registry.schema.json` are created under
  `src/AutoContext.Engine/Resources/` with their end-state names —
  **not** renamed or moved from today's
  `src/AutoContext.Mcp.Server/mcp-workers-registry.json`. That legacy
  file stays in place, untouched under its old name, serving the
  still-live `AutoContext.Mcp.Server` until Phase 15 deletes the whole
  project (the same copy-into-the-engine pattern Phase 5 used for the
  instruction corpus — the old consumer keeps working under the old
  name; the engine's copy is born correctly named in the project that
  owns it). `McpToolsRegistryLoader` reads it from `Resources/` via
  `AppContext.BaseDirectory`; `McpToolsRegistrySchemaValidator`
  validates it against the embedded schema at both build time and
  load time.
- **REMOVED** (the build-time projection model was replaced by a
  hand-authored `mcp-tools-catalog.json` + its schema; see the *New
  direction* note above — the paragraph below is retained as
  historical record of what row 5 originally shipped, since deleted).
  The engine wiring has been removed: the
  `AutoContext.McpTools.Manifest.Generator` `ProjectReference`, the
  `McpToolsManifestGenerator.targets` import, the gitignored
  `Resources/mcp-tools.json` output, and its `.gitignore` entry are all
  gone, and the generator project and its test project have been
  deleted from the tree.
  Historically, build-time projection of
  `mcp-tools.json` (wire shape only; the per-request `disabled` filter
  is layered by the engine at runtime) emitted into
  `src/AutoContext.Engine/Resources/` from the registry above. A dedicated `AutoContext.McpTools.Manifest.Generator`
  console tool (`mcp-tools-manifest-gen`) — mirroring the
  `workers.json` generator's structure, source-gen serializer, and
  `WriteIfChanged` byte-stability — reads `mcp-tools-registry.json`,
  flattens the worker groups into one flat tool list (preserving
  registry declaration order), and carries each tool's `name`,
  `description`, and task `name`s forward. It drops the registry's
  input `parameters` (the `McpTools.List` wire shape omits input
  schemas) and per-task `editorconfig` bindings (dispatch metadata).
  A missing, unparsable, or empty registry, a tool or task without a
  name, a tool without a description, or a duplicate tool name all
  fail the build. The generator runs from a `.targets` imported by
  `AutoContext.Engine.csproj`; the output is gitignored (no
  source-side copy).
- `McpTools.List` handler over the `mcp-tools-registry.json` data,
  filtered per-request by each tool's `disabled` flag from the config
  snapshot (per-tool granularity; the task concept — and its
  legacy per-task filter — is gone with the flatten).
- `McpTools.Invoke` handler: schema-validate `arguments` against the
  tool's `inputSchema`, dispatch to the worker, marshal the worker
  response into the discriminated envelope (`ok`/`tool-error`/
  `schema-error`/`disabled`/`not-found`). Cancellation forwards
  through the existing `IMcpTask` token.
- Cross-process worker pipes stay on the existing worker-control
  contract (now living in `AutoContext.Workers.Core` after the
  Phase 0 consolidation; workers themselves are not absorbed).

**Tests**:
- `mcp-tools-registry.json` schema-validates at build time; a
  malformed registry fails the build.
- `McpTools.List` reflects the registry, filtered by disabled state
  from `Config.Get`; toggling config fans out via
  `Config.Subscribe` and a subsequent `List` reflects the change.
- `McpTools.Invoke` happy path: dispatched to the right worker per
  the registry's `workerId` field; response composed into the wire
  envelope; `content` block-for-block matches the worker payload
  (P1 — same shape regardless of transport).
- `schema-error` on malformed `arguments`.
- `tool-error` on a worker reporting failure.
- `disabled` / `not-found` envelopes carry identity only, not the
  schema or description.
- Cancellation: caller cancels mid-invoke; worker's `IMcpTask`
  observes the token and returns the cancellation envelope cleanly.
- `WorkerManager` `ensureRunning` semantics: two concurrent
  `Invoke`s against the same worker race once into one spawn.

**Out of scope**: MCP-server-only role over stdio (Phase 11 — it
reuses these same handlers).

## Phase 8 — Worker → engine logging integration

**Status**: Completed on branch `features/worker-engine-logging`.

| # | Commit subject | State |
|---|---|---|
| 1 | `refactor(workers): rename Framework.Workers to Workers.Core` | DONE |
| 2 | `feat(engine-core): serve Engine.WriteLog as a fire-and-forget notification routed by category to per-worker logs` | DONE |
| 3 | `feat(workers-core): add worker→engine log sender with bounded ring and stderr drop fallback` | DONE |
| 4 | `feat(engine): serve Logs.GetWorker and TailWorker and capture worker stderr` | DONE |
| 5 | `refactor(framework-logging): delete the legacy worker→extension sideband sink` | DONE |
| 6 | `docs(plan): mark Phase 8 complete` | DONE |

**Commit grouping.** The ladder collapses the twelve fine-grained steps
into six coherent commits — the fewest that still keep every commit
green at its boundary and reviewable on its own. Each grouped commit is
a single behavioural unit with one dominant Conventional Commits type:

- **Row 2** folds the fire-and-forget notification prelude into its
  first (and only) consumer: adding `NotificationHandlerResult` +
  processor support is worthless without the `WriteLogRpcHandler` that
  uses it, and the handler's tests exercise the notification path, so
  the infrastructure and its consumer land and are tested as one. This
  also brings the engine-side category-prefix routing to per-worker
  logs (`LogFileSinkService` + `EngineCacheLayout.WorkerLogFilePath`),
  since that is what makes the ingested records land correctly.
- **Row 3** folds the whole worker-side sender quartet
  (`EngineWriteLogClient`, `AddEngineLoggerProvider` /
  `EngineLoggerProvider`, `EngineLogIngestRing`) into one commit — they
  are meaningless apart (a provider with no client, a ring with no
  producer) and are unit-tested together against a fake engine.
- **Row 4** completes the read side (`Logs.GetWorker` +
  `Logs.TailWorker` are symmetric `LogsRpcHandler` extensions) and the
  engine-side worker-stderr capture, then proves the full
  worker→engine→read path with the cross-process integration test that
  was previously its own row. The test lands here because this is the
  boundary at which the pipeline is first end-to-end observable.
- **Row 5** deletes the legacy sideband only after row 4 has proven the
  replacement path, keeping the cutover bisectable on its own.
- **Row 6** is the standard docs mark-complete step.

**Row 1 note (landed).** The dependency-direction reorg that Phase 8
depends on: `AutoContext.Framework.Workers` was renamed to
`AutoContext.Workers.Core` (folder, csproj, namespaces, every consumer,
`AutoContext.slnx`, and the `AutoContext.Workers.Core.Tests` test
project), so a `Framework.*` project no longer depends on `Engine.*`.
The worker-side log sender (row 3) therefore lands in
`Workers.Core/Logging/`, and `Framework.Logging` sheds its legacy sideband
in row 5 (which then also moves `CorrelationScope` to `Workers.Core` and
retires the emptied project). Verified green via
`.\build.ps1` (both stacks). See the *Renames since this plan was first
written* map above.

**Goal**: every `ILogger<T>` record a worker emits ships via
`Engine.WriteLog` to the engine, gets routed by `category` prefix to
the right `worker-<workerId>.log`, fans out on `logs` and
`Logs.Tail*`, with bounded ring buffering and stderr fallback when
the engine is briefly unreachable.

**Design anchors**: `§ RPC surface` (`Engine.WriteLog`, `Logs.GetWorker`,
`Logs.TailWorker`), `§ Log pipeline backpressure` pitfall,
`§ Worker–engine connectivity` pitfall, the *Log categories* table.

**Code touch**:
- **Worker side — `AutoContext.Workers.Core/Logging/`** (new folder;
  the worker-side sender quartet). `AddEngineLoggerProvider` +
  `EngineLoggerProvider` — an `ILoggerProvider` that wraps `ILogger<T>`
  records into the canonical `JsonLogRecord` envelope
  (owned by `Engine.Protocol`) and dials the engine's `rpc` pipe for
  `Engine.WriteLog` notifications. Lives in `Workers.Core` — **not**
  `Framework.Logging` — because marshalling `JsonLogRecord` needs
  `Engine.Protocol`, and a `Framework.*` project must never depend on
  `Engine.*`; `Workers.Core` already dials the engine, so the
  dependency is correct there.
- `EngineWriteLogClient` (`Workers.Core/Logging/`) — typed client for
  the `Engine.WriteLog` RPC; dials the engine `rpc` pipe with the
  `Engine.Hello` handshake (same client→engine direction the
  `WorkerHealthMonitorService` already uses for the `health` pipe).
- `EngineLogIngestRing` (`Workers.Core/Logging/`) — worker-side bounded
  in-memory ring (default 1000 records / 1 MiB, drop-oldest on
  overflow), retry with exponential backoff, replay on reconnect. On
  drop, one line to **stderr** per drop batch
  (`engine log dropped N records`).
- **Engine side — extend the existing `AutoContext.Engine.Core/Logging/`
  pipeline** (do **not** build a parallel one): worker records enter the
  existing single `LogChannel`; `LogFileSinkService` gains
  category-prefix routing (`worker.<workerId>.*` →
  `worker-<workerId>.log`, else `engine.log`) with lazy per-worker
  appenders; `EngineCacheLayout` gains a `WorkerLogFilePath(workerId)`
  resolver; the shared `Broadcaster<JsonLogRecord>` already fans out to
  the `logs` pipe, so `Logs.TailEngine`/`TailWorker` filter by
  `category`; a worker-log reader (mirroring `LogFileReader`)
  backs `Logs.GetWorker`.
- **Prelude — inbound fire-and-forget notification support (row 2).**
  `Engine.WriteLog` is a true JSON-RPC 2.0 notification (no `id`, no
  response), but `RpcConnectionProcessor` today knows only
  `UnaryHandlerResult` / `StreamingHandlerResult` — both of which
  always write a response frame. Add a third `RpcHandlerResult` shape
  (`NotificationHandlerResult`: `Continuation.Continue`, no payload)
  and teach the processor to route an id-less request to its handler
  and write **no** response. This mirrors the Phase 3 server-streaming
  prelude and is shared infrastructure Phase 10's `Agent.*`
  fire-and-forget notifications reuse.
- **Engine-side `Engine.WriteLog` handler — a dedicated
  `Rpc/Handlers/WriteLogRpcHandler`** (decided — **not** an `Engine.*`
  grab-bag handler, **not** folded into `LogsRpcHandler`). Rationale:
  the codebase names handlers by capability, not wire-prefix
  (`Engine.RegistryEntries` → `RegistryRpcHandler`; `Hello`/`Shutdown`
  are policy-level), and log **ingest** (producer; notification; no
  response; depends only on `LogChannel`) is a distinct capability from
  log **read** (`LogsRpcHandler` — consumer; request/response +
  streaming; depends on `LogFileReader` + the logs broadcaster).
  The handler stays paper-thin (P1): deserialise `JsonLogRecord`,
  enqueue into `LogChannel`, return the no-response outcome. Routing by
  `category` prefix is downstream in `LogFileSinkService`; `LogChannel`
  is MPSC-safe, so this second producer (alongside the engine's own
  `EngineLoggerProvider`) preserves the single-reader drain (P9).
- Engine supervises worker stderr via `Process.Start` and emits each
  captured stderr line under category
  `worker.<workerId>.engine.stderr`, landing in the right per-worker
  file by the prefix rule.
- `Logs.GetWorker` / `Logs.TailWorker` handlers on `LogsRpcHandler`
  (extends its `Methods` set) — `not-found` discriminated envelope
  distinguishes "this `workerId` was never spawned" from empty
  `records`.
- **Delete the `Framework.Logging` legacy sideband and retire the project**
  (row 5): `PipeLogger`, `PipeLoggerProvider`, `LoggingClient`, `LogEntry`,
  `JsonLogEntry`, `LogServerJsonContext`, `JsonLogGreeting` — the
  worker→extension `LogServer` path is replaced wholesale by
  `Engine.WriteLog`. Row 5 also swaps the worker host onto
  `AddEngineLoggerProvider` (the engine derives its own `rpc` address and
  hands it to each spawned worker as `--service log=<address>`), and moves
  the surviving `CorrelationScope` helper into `AutoContext.Workers.Core`
  next to its only consumer — emptying and deleting the `Framework.Logging`
  and `Framework.Logging.Tests` projects.

**Tests**:
- Worker `ILogger<T>` record arrives in the right per-worker log
  file under the expected category prefix.
- Engine-side fan-out: a subscriber on `logs` sees both engine and
  worker records with the same envelope.
- `Logs.GetWorker("never-spawned")` returns `not-found`;
  `Logs.GetWorker("spawned-but-quiet")` returns `ok` with empty
  `records`.
- Worker can't reach engine on cold start: records buffer into the
  ring; on engine availability the ring drains in order.
- Ring overflow: oldest records drop; one stderr line per batch;
  next successful drain carries a synthetic
  `engine.logging`/`warning` "dropped N worker log records" record.
- Slow `Logs.Tail*` subscriber is dropped with the terminal
  `dropped` frame; the file sink and other subscribers keep going.
- Worker stderr (a print that bypasses the logger) shows up under
  `worker.<id>.engine.stderr` in the worker's log file.

**Out of scope**: any on-disk worker spool — there isn't one, by
design.

## Phase 9 — Discovery

**Status**: Completed on branch `features/discovery`.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(engine-core): serve Discovery.RouteForPrompt and RouteForTool` | DONE |
| 2 | `docs(plan): mark Phase 9 complete` | DONE |

**Commit grouping.** Discovery is a single read-only service over state
the engine already owns (Phase 6 `Instructions.Subscribe`, Phase 7
`McpTools.List`), so the whole feature lands as one green, reviewable
commit rather than an artificial ladder. Row 1 carries `DiscoveryService`
(the *category → tool* and *extension → instructions file* indices, built lazily
over already-owned state and filtered by the current disabled state read
per query), the prompt/extension scan logic ported from the `.cjs` hook,
the `Rpc/Handlers/DiscoveryRpcHandler` serving both `Discovery.*` methods
(`RouteForPrompt` is prompt-driven; `RouteForTool` intersects the tool's
`ActivationFlags` with each instructions file's), the `Messages/Discovery/`
DTOs and their source-generated JSON contexts, and the routing tests.
Splitting messages, service, and handlers into separate commits would only
produce intermediate states that are meaningless on their own — a handler
with no service, a DTO with no producer — so grouping them keeps every
commit boundary both green and coherent. Row 2 is the standard docs
mark-complete step.

**Goal**: engine builds the *category → MCP tool* and *extension →
instructions file* indices from already-owned state and answers
`Discovery.RouteForPrompt` / `Discovery.RouteForTool`. The `.cjs`
hooks (Phase 14) stop carrying their own scan logic.

**Design anchors**: `§ RPC surface` (`Discovery.*`), `§ P7`, `§ P11`.

**Code touch**:
- `AutoContext.Engine.Core/Features/Discovery/` — a **P11 capability**
  (the engine boots without it; it serves the `Discovery.*` RPCs
  outward), so it lives under `Features/` beside `Instructions/` and
  `McpTools/`, **not** at the engine-library root. It composes the
  read-only `IInstructionsManifestAccessor`, `IMcpToolsRegistryAccessor`,
  and `IConfigSnapshotAccessor` snapshots — a capability→capability read,
  which P11 permits (only substrate→capability is forbidden).
  - `DiscoveryService` — owns the two indices and the two routing
    queries. The bundled corpus and tool registry are immutable at
    runtime, so the *structural* indices never change: the service
    builds them **lazily once** (first query, after the startup loaders
    have populated the accessors) and reads `IConfigSnapshotAccessor.Current`
    **per query** for the disabled filter — always current, so no hosted
    service and no change subscription are needed.
  - `CategoryIndex` — inverts each `McpToolsRegistryEntry.Category` into
    *category-name → tool-names*, keyed under the tool's own category
    **and every ancestor category** (walking the catalog `Parent`
    chain), so a broad prompt word like `.net` surfaces the whole
    family (C#, NuGet). `Match(prompt)` runs the word-boundary literal
    scan (the same shape as today's `.cjs`) → matched categories + tool
    names.
  - `ExtensionIndex` — builds *extension → instructions-file names* from
    each `InstructionsFileManifestEntry.Extensions`. `Match(prompt)`
    runs the `\.[A-Za-z][A-Za-z0-9]{0,12}` regex → matched extensions +
    file names.
- `Rpc/Handlers/DiscoveryRpcHandler` — the `IRpcMethodHandler` serving
  `Discovery.RouteForPrompt` / `RouteForTool`, delegating to
  `DiscoveryService` (one handler per family, matching the shipped
  convention — the target tree's older `Discovery/DiscoveryHandlers.cs`
  sketch is superseded).
- `AutoContext.Engine.Protocol/Messages/Discovery/` — the wire DTOs, one
  type per file (mirroring `Messages/McpTools/`): the method-name
  constants, `JsonDiscoveryRouteForPromptParams` (`{ prompt }`),
  `JsonDiscoveryRouteForPromptResult`
  (`{ matchedCategories[], matchedExtensions[], tools[], instructions[] }`),
  `JsonDiscoveryRouteForToolParams` (`{ name }`), and
  `JsonDiscoveryRouteForToolResult` (`{ instructions[] }`). Each is
  registered in `Serialization/ProtocolJsonContext`.
- **`RouteForPrompt(prompt)` is purely prompt-driven.** It answers "what
  did the user reference", not "what is in the workspace" — a prompt
  naming `c#` in a repo with no C# still surfaces the C# tools/files
  (the user may be about to add C#). Workspace-narrowing is a *separate*
  concern owned by `Instructions.List`'s `applyToWorkspaceFilter` and is
  deliberately **not** folded into routing, so each method answers one
  clean question. Results are filtered by the current disabled state.
- **`RouteForTool(toolName)` bridges via activation flags.** MCP-tool
  categories and instructions-file categories are separate taxonomies,
  and tools carry no extensions, so there is no direct tool→file link.
  Both sides *do* carry workspace-context `ActivationFlags` (the Phase 4
  vocabulary — `hasDotNet`, `hasCSharp`, …), so `RouteForTool` returns
  every instructions file whose `ActivationFlags` intersect the tool's
  (e.g. `analyze_csharp_code` → the `hasDotNet`/`hasCSharp` family),
  filtered by disabled state. Accepted consequence: an instructions file
  with **no** activation flags (cross-cutting guidance such as
  `testing`, `code-review`, `git-commit`) never surfaces from
  `RouteForTool` — those are always-attached or surface only via
  `RouteForPrompt`, so the omission is by design, not a bug.

**Tests**:
- `CategoryIndex`: ancestry-inclusive keying (a `C#` tool matches both
  `c#` and `.net`); word-boundary scan (matches `c#` in "port to c#"
  but not inside "abc#def").
- `ExtensionIndex`: `.cs` / `.ps1` extracted from prompt text map to
  the files whose `applyTo` names that extension.
- `RouteForPrompt` returns matched categories + extensions + the union
  of tools and instructions files; disabled tools / files are excluded;
  a workspace-absent language named in the prompt still surfaces.
- `RouteForTool` returns the activation-flag-intersecting files;
  flagless files never appear; disabled files are excluded.
- Disabled-state changes are reflected on the next query without any
  index rebuild (the per-query config read).

**Out of scope**: hook integration (Phase 14).

## Phase 10 — Agent.* RPCs

**Status**: Completed on branch `features/agent-rpcs`.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(engine-core): serve Agent.* notifications and Agent.Events.Subscribe` | DONE |
| 2 | `docs(plan): mark Phase 10 complete` | DONE |

**Commit grouping.** The `Agent.*` surface is a single fire-and-forget
fan-out over infrastructure the engine already owns — Phase 8's inbound
notification handling (`NotificationHandlerResult`), Phase 3's
server-streaming, and the shared `Infrastructure/Events/Broadcaster<T>`
— so the whole feature lands as one green, reviewable commit rather than
an artificial ladder. Row 1 carries the `Messages/Agent/` DTOs and their
source-generated JSON contexts, the `Features/Agent/` fan-out
(`AgentEventFrameStream` draining a shared `Broadcaster<JsonAgentEvent>`),
the `Rpc/Handlers/AgentRpcHandler` serving the five notifications and
`Agent.Events.Subscribe`, the DI wiring, and the round-trip /
slow-subscriber / concurrent-subscriber tests. Splitting the DTOs, the
stream, and the handler into separate commits would only produce
intermediate states that are meaningless on their own — a subscribe
stream with no producer, a notification handler with nothing to fan out
to — so grouping them keeps every commit boundary both green and
coherent. Row 2 is the standard docs mark-complete step.

**`AgentSessionToolHistogram` deferred.** The target tree lists an
in-memory per-session `ToolUsed` histogram, but its only consumer
(`Diagnostics.Run`) is out of scope for this release. Building an
in-memory tally nothing reads would be dead scaffolding-ahead (against
the *Just-in-time scaffolding* ground rule), so `ToolUsed` re-broadcasts
like the other four notifications and the histogram lands with the
`Diagnostics.Run` phase that first reads it. `Agent.ToolUsed` carries
`sessionId` alongside `toolName`/`outcome` (like the other four
session-scoped events), so that future histogram already has its
per-session key on the wire.

**Goal**: engine accepts the agent-loop notifications hooks fire
(`SubagentStarted`/`SubagentStopped`/`Compacted`/`ToolUsed`/`TurnEnded`)
and re-broadcasts them on `Agent.Events.Subscribe`. UX-only;
fire-and-forget; lost events tolerable (per the design).

**Design anchors**: `§ RPC surface` (`Agent.*`), `§ P6` (subscription
shape), `§ P10` (cross-process fan-out).

**Code touch**:
- `AutoContext.Engine.Core/Features/Agent/AgentEventFrameStream` over a shared
  `Infrastructure/Events/Broadcaster<AgentEvent>` — same
  per-subscriber bounded-buffer / slow-subscriber-drop discipline
  Phase 2 introduced.
- The five notification handlers, each mapping its inbound params onto
  the unified `JsonAgentEvent` envelope the broadcaster fans out.
- The per-session `ToolUsed` histogram is **deferred** to the
  `Diagnostics.Run` phase that first consumes it (see the grouping note
  above): its consumer is out of scope, so building it here would be
  scaffolding-ahead. `Agent.ToolUsed` still carries `sessionId` on the
  wire so that future histogram has its per-session key.

**Tests**:
- Notification → broadcast round-trip per event family.
- Slow subscriber on `Agent.Events.Subscribe` is dropped; producer
  is never back-pressured.
- Two clients subscribed concurrently see the same envelope sequence.

**Out of scope**: hook script integration (Phase 14);
`Diagnostics.Run` consumer.

## Phase 11 — MCP-server-only role

**Status**: Completed on branch `features/mcp-server-stdio-role`.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(engine): serve instructions and mcp-tools over the stdio mcp-server role` | DONE |
| 2 | `feat(engine): add the Instructions.SearchByMetadata capability and search_instructions_by_metadata tool` | DONE |
| 3 | `test(engine): smoke the stdio mcp-server role end-to-end` | DONE |
| 4 | `docs(plan): mark Phase 11 complete` | DONE |

**Commit grouping.** The `--mcp-server with-stdio` role split already
landed as Phase 1 scaffolding — `EngineCommand` parses the flag, rejects
every daemon-only switch under the MCP role (`TryFindDaemonOnlySwitch`),
and `Program.cs` dispatches into `McpServerHostFactory`, which today is a
stub that exits non-zero with a "not implemented yet" diagnostic
(asserted by `ProgramTests`). Phase 11 does **not** rebuild the argv role
split; it fills that stub. What remains is one coherent behavioural unit
— a second, state-free composition of handlers the engine already owns —
so it lands as few commits as stay individually green rather than an
artificial per-file ladder. Row 1 carries the whole role: the
`McpServerHostFactory` fill-in composing
`AddMcpServer().WithStdioServerTransport()`, the root-level
`Engine.Core/McpServer/` transport shim (sibling of `Rpc/` + `Endpoints/`,
**not** a Feature: the `tools/list` + `tools/call` bridge + an
`InputSchemaBuilder`, porting the shape today's
`AutoContext.Mcp.Server/Tools/McpSdkAdapter` proves out), an
`IConfigReloader` seam on `ConfigFileManager` that re-reads `.autocontext.json`
per request, and the registration of the Phase 6 (`Instructions.*`) and
Phase 7 (`McpTools.*`) capability services as the stdio tool surface.
The instruction tools are grouped behind an `McpServer/Tools/InstructionsToolSource`
(one `IMcpTool` leaf per tool); the adapter is a generic router over the
registered `IMcpToolSource`s and knows no concrete tools.
Row 1's instruction surface is exactly the tools that already have engine
handlers to shim over — `list_instructions` (`Instructions.List`),
`search_instructions_by_content` (`Instructions.SearchContent`),
`get_instructions` (`Instructions.Get`) — plus every `McpTools`
`analyze_*` / `read_*` tool. It also flips the
existing `ProgramTests` stub assertion to the real role and ships the
in-process unit tests for the composable pieces — the `tools/list` /
`tools/call` adapter mapping, the `InputSchemaBuilder`, the
`InstructionsToolSet`, and the config reload seam. Splitting that into "adapter",
"registration", and "tool-surface" commits would only produce
meaningless intermediate states — a host that serves no tools, tools
with no transport — so grouping them keeps every boundary green and
reviewable. Row 2 adds the one instruction surface the engine does **not**
yet own as a handler: `search_instructions_by_metadata` is backed by a
metadata **predicate** matching engine (typed fields, regex/glob/equality,
`unknown-field` / `type-mismatch` / `invalid-regex` / `pattern-too-long`
error envelopes, section AND-intersection) that lives only in the TS
extension today and is listed as *future* (`McpTools.SearchByMetadata`) in
the design. Because a transport shim must not re-implement matching, row 2
first ports that engine into `Engine.Core/Features/Instructions/` (the
predicate evaluator + apply-to matcher + metadata-view assembly) behind a
new `Instructions.SearchByMetadata` capability the **pipe RPC reuses too**,
then registers the `search_instructions_by_metadata` stdio tool on top of it
— keeping the two surfaces byte-identical by construction. It lands after
row 1 because it is additive to the adapter row 1 builds, and carries a
small design delta promoting `SearchByMetadata` from future to present.
Row 3 is the end-to-end smoke test (gated `Category=Smoke`,
matching the phase-4 `Workspace.Detect` row and the phase-15 regression
set): it spawns the real `autocontext-engine --mcp-server with-stdio`
binary and drives `tools/list` / `tools/call` over actual stdio —
proving the process-boundary behaviour no in-process test can, namely
the P1 cross-transport byte-identical `tools/call` diff against the pipe
`McpTools.Invoke`, no-pipe-bind coexistence with a parallel daemon,
per-request `.autocontext.json` re-read, clean stdio-EOF exit, and no
`engine-registry.json` entry. It lands after rows 1–2 because it spawns
what they build. Row 4 is the standard docs mark-complete step.

There is deliberately **no** legacy-server cutover row here.
`AutoContext.Mcp.Server` stays untouched as the legacy stdio server
until Phase 15 retires it — it is **not** shrunk to a shim that
delegates to the engine's role. A delegating shim would forward into
the same engine code row 1 builds, so it is not an independent
regression signal, and row 3 already smoke-tests the engine's stdio
role end-to-end over a real process boundary. Leaving the legacy
server as-is keeps its still-legacy consumers working unchanged — the
extension's `servers.json`-driven provider and the packaging layout —
until Phase 13 (packaging) and Phase 14 (extension) repoint them at
the engine binary and Phase 15 deletes the project wholesale.

**Goal**: `autocontext-engine --mcp-server with-stdio` runs the
reduced stdio MCP server — the daemon's read capabilities plus
on-demand worker dispatch, and nothing else. No daemon pipes, no
registry entry, no `engine.log`, no `FileSystemWatcher`, no
keep-alive / idle clock. Worker-backed tools (`analyze_*` /
`read_*`) spawn their worker lazily over a **private** dispatch
pipe namespaced by an ephemeral, internally-minted instance id
(never from argv); the worker stays warm for the process lifetime
and is killed on stdio EOF. Per-request disk read of
`.autocontext.json`. Stdio EOF exits cleanly.

**Design anchors**: `§ Engine binary` (role split),
`§ Engine options (CLI surface)` (MCP-server argv subset),
`§ Lifecycle` (*MCP-server-only role is out of scope*),
`§ MCP-server role argv discipline` pitfall.

**Code touch**:
- `AutoContext.Engine/McpServerHostFactory.cs` — fills the stub,
  composing `AddMcpServer().WithStdioServerTransport()` plus the
  reduced-but-sufficient service set: the `Instructions.*`
  in-process services, and — for worker-backed tools — the
  `McpToolsRegistryAccessor` + `McpToolsInvoker` +
  `WorkerProcessService` over an ephemeral, internally-minted
  instance id. **No** daemon pipe host, endpoint host,
  subscription broadcasters, registry-file writer, `engine.log`
  sink, watchdogs, or `FileSystemWatcher`.
- Argv parser rejects `--instance-id`, `--instance-label`,
  `--idle-timeout`, `--parent-pid`, `--retention`, `--log-rotation` in
  the MCP-only role with a stderr error and non-zero exit.
- The same handler code from Phase 6 (`Instructions.*`) and Phase 7
  (`McpTools.*`) is registered as `instructions_*` and the existing
  `analyze_*` / `read_*` MCP tools (today's surface). The per-request
  `.autocontext.json` read is wired into the handler dependency
  graph for this role.
- `AutoContext.Mcp.Server` is left **untouched** as the legacy stdio
  server — **not** shrunk to a delegating shim. The engine's stdio
  role stands on its own (proven end-to-end by row 3's smoke test),
  so a shim forwarding into the same engine code would add no
  independent coverage. The legacy server keeps serving its
  `servers.json`-driven consumers unchanged until Phase 13
  (packaging) and Phase 14 (extension provider) repoint them at the
  engine binary and Phase 15 deletes the project.

**Tests**:
- **In-process (row 1).** The adapter routes `tools/call` by name to the
  matching `IMcpTool` leaf and maps the response onto the MCP SDK shapes;
  `InputSchemaBuilder` renders each tool's parameters; the instruction
  leaves and the `RegistryMcpTool` translate their arguments and marshal
  into the shared `Instructions.*` / `McpTools.*` handlers; the
  `IConfigReloader` seam re-reads `.autocontext.json` on each call.
  (Daemon-only-switch rejection is already covered by the Phase 1
  `EngineCommand` argv tests — the role split shipped there.)
- **Smoke, gated `Category=Smoke` (row 3)** — spawns the real
  `autocontext-engine --mcp-server with-stdio` binary and drives it
  over actual stdio:
  - `tools/list` and `tools/call` return byte-identical `content` for
    the same input as the pipe `McpTools.Invoke` (P1 cross-transport
    diff test).
  - Stdio mode binds none of the four daemon pipes and writes no
    `engine-registry.json` entry (asserted with a parallel daemon
    on the same workspace — they coexist; any worker-dispatch
    pipes the stdio role opens are private, namespaced by its
    ephemeral instance id, so they never collide with the daemon).
  - Per-request disk re-read: a write to `.autocontext.json` from a
    parallel daemon is observed on the next stdio request.
  - Stdio EOF exits cleanly.

**Out of scope**: deleting `AutoContext.Mcp.Server` (Phase 15);
extension's MCP server definition repointing (Phase 14).

## Phase 12 — `Client.Core` (CLI-as-library) and `EngineDaemonManager` (TS)

**Status**: Completed on branch `features/engine-client-libraries`.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(client-core): scaffold dialer, handshake, and find-or-spawn` | DONE |
| 2 | `feat(client-core): add typed rpc clients and subscription consumers` | DONE |
| 3 | `test(client-core): round-trip every rpc against an in-process engine` | DONE |
| 4 | `feat(nodejs-core): add rpc-exchange and events-subscription pipe clients` | DONE |
| 5 | `feat(nodejs-core): add EngineDaemonManager with find-or-spawn and typed rpc` | DONE |
| 6 | `test(nodejs-core): round-trip every rpc against a spawned engine binary` | DONE |
| 7 | `docs(plan): mark Phase 12 complete` | DONE |

**Commit grouping.** Rows 1–3 implement **12a** (`AutoContext.Client.Core`,
the .NET CLI-as-library; public surface
`AddAutoContextClient(Action<ClientOptions>)`, references `Framework.Pipes` +
`Engine.Protocol`, dials the pipes it never binds). Rows 4–6 implement
**12b** (the TS `EngineDaemonManager`, a plain class under
`Nodejs.Core/src/engine/`). The two deliverables are independent — no row
in one half depends on a row in the other — and can land on parallel
branches; they are grouped into one phase only because both first need
the full engine wire surface from Phases 1–11.

Within 12a, row 1 folds the project scaffold, the four-pipe dialer, the
`Engine.Hello` exact-match handshake with its `EngineProtocolException`
refusal, and the find-or-spawn / cold-start connect budget into one
"can reach an engine" unit — the scaffold is not a standalone commit (a
bare `AddAutoContextClient` registering nothing is scaffolding-ahead) and
the dialer, handshake, and spawner are meaningless apart. Row 2 folds the
typed unary / notification RPC clients and the `IAsyncEnumerable`
subscription consumers into one "typed surface over the dialer" layer —
each RPC family's client is thin marshalling over the same seam. Row 3
keeps the cross-cutting round-trip suite (in-process engine via
`AddAutoContextEngine`) as its own `test(...)` commit — the authoritative
conformance gate reads best as a distinct "here is the evidence it's
right" diff.

Within 12b, row 4 adds the two framed pipe clients the TS substrate still
lacks (today `pipes/` carries only the liveness keep-alive and passive
streaming clients — the `rpc` request/response and `events` subscription
shapes are new), the transport floor the manager needs. Row 5 folds the
manager's find-or-spawn / supervision and its typed RPC-method +
subscription surface into one class — splitting "add the class" from
"give it methods" leaves a manager that does nothing in between. Row 6
keeps the round-trip suite (a spawned `autocontext-engine` binary via
`child_process`) as its own `test(...)` commit, symmetric with row 3.

Each feature commit ships its own unit tests; rows 3 and 6 are the
cross-cutting round-trip gates that exercise cold spawn, warm reuse,
snapshot-on-subscribe streams, slow-subscriber drop, and the
protocol-version-mismatch refusal. Row 7 is the standard docs
mark-complete step.

**Goal**: two independent deliverables that happen to land together
because both first need the engine's wire surface from Phases 1–11.
They are **not** parallel implementations of one concept — they
have different responsibilities, different consumers, and only
share the engine's wire contract.

- `AutoContext.Client.Core` (.NET) — the `autocontext` CLI as a
  library. **Created in this phase** — first consumer is the typed
  RPC surface this phase introduces — alongside its sibling test
  project `AutoContext.Client.Core.Tests` (both added to
  `AutoContext.slnx` and `build.ps1`). Houses every type the CLI
  binary uses internally (`EngineClient` typed-RPC surface,
  four-pipe dialer, cold-start-or-attach resolver, subscription
  consumers, `IEngineSpawner`). References `Framework.Pipes` +
  `Framework.Protocol`. Consumers:
  `AutoContext.CommandLine` and third-party .NET embedders that
  want CLI-shaped behaviour in-process. See
  [`autocontext-cli.md`](future/autocontext-cli.md) for the
  full CLI-as-library picture.
- `EngineDaemonManager` (TS, `src/AutoContext.Nodejs.Core/src/engine/`) —
  owns engine-daemon lifecycle on the TS host side (find-or-spawn
  against the bundled `autocontext-engine` binary, supervise the
  child, tear down on host shutdown) **and** exposes the engine's
  RPC surface as typed methods on top of that lifecycle. Consumers:
  the VS Code extension (Phase 14) and the agent-plugin `.cjs`
  hook scripts (Phase 14).

**Design anchors**: `§ Composition contracts`, `§ Sharing principle`,
`§ Lifecycle` (cold start, warm reuse), `§ P6`/`§ P9`/`§ P10`.

**Code touch**:
- `AutoContext.Client.Core/`:
  - Pipe dialer (`rpc` + `events` only by default; `health` and
    `logs` opt-in).
  - Find-or-spawn flow over a `IEngineSpawner` seam (concrete
    impl: `Process.Start` against the bundled binary). Cold-spawn
    retry with the doc's connect-budget shape.
  - `Engine.Hello` handshake; refusal surfaces as a typed
    `EngineProtocolException`.
  - Typed RPC clients for every surface: `Config.*`, `Instructions.*`,
    `Workspace.*`, `McpTools.*`, `Discovery.*`, `Agent.*`, `Logs.*`,
    `Engine.Lifecycle`, `Engine.RegistryEntries`,
    `Engine.Shutdown`. Note: `Engine.WriteLog` is **not** exposed
    on `Client.Core`'s typed surface — it is a worker→engine
    notification owned by `Workers.Core`
    (`EngineWriteLogClient` + `AddEngineLoggerProvider`); the wire
    DTO itself lives in `Framework.Protocol` so both sides marshal
    the same envelope.
  - Subscription consumers as `IAsyncEnumerable<T>` (P6 — first-class
    subscriptions; P8 — async I/O end-to-end).
- `AutoContext.Nodejs.Core/src/engine/engine-daemon-manager.ts` —
  plain TS class `EngineDaemonManager` (no DI container per
  `§ Sharing principle`). Same wire surface, same RPC names, same
  envelope shapes; additionally owns find-or-spawn against the
  bundled `autocontext-engine` binary and supervises the child
  process for the lifetime of the host.
- TS pipe-client substrate lives in
  `AutoContext.Nodejs.Core/src/pipes/` (today's location
  `AutoContext.Nodejs.Core/src/pipes/`, moved as part of the
  Phase 0 rename); extended where the four-pipe shape needs it.

**Tests**:
- .NET client round-trips every RPC against an in-process engine
  composed via `AddAutoContextEngine`.
- TS client round-trips every RPC against a spawned engine binary
  (Vitest + `child_process`).
- Cold spawn: client connects, no engine present, spawner fires,
  client retries within the connect budget, handshake succeeds.
- Warm reuse: two clients of the same launcher (same `--instance-id`)
  see one engine.
- `*.Subscribe` streams: snapshot-on-subscribe, revision counter,
  late-subscriber correctness.
- Slow-subscriber on the client side disconnects with `dropped`
  rather than back-pressuring the engine.
- Engine refusal on protocol-version mismatch surfaces as a typed
  error on both clients.

**Out of scope**: the extension and the hooks consuming the client
(both Phase 14); CLI verb implementations (`autocontext-cli.md`,
separate plan).

## Phase 13 — Distribution and packaging

**Status**: Completed on branch `dev/autocontext-engine`.

**Goal**: `scripts/package.ps1` emits per-RID engine staging under
`artifacts/engine/<rid>/...`; per-platform packaging (VSIX, plugin
release, GitHub-release tarball) selects the matching RID and
copies the flat `engine/` subtree into the shipped artefact. The
engine resolves its side-cars from `AppContext.BaseDirectory`
without any host-supplied path.

**Design anchors**: `§ Distribution`, `§ Distributed bundle layout`,
the per-platform packaging note (`vsce package --target <target>`).

**Code touch**:
- `scripts/package.ps1` / `AutoContext.Build` module — per-RID engine
  publish (`dotnet publish -r <rid> --self-contained`), per-worker
  self-contained publish into `Workers/<id>/`, manifest copy
  (`Instructions/`, `Resources/`), per-platform packaging that
  selects one RID's staging into one VSIX / one plugin release /
  one tarball.
- `package.json` (`AutoContext.VsCode/`) — `vsce package` invocation
  shifts to `--target <target>` per supported platform; the
  `engine/` directory replaces today's per-RID layout under the
  extension root.
- Engine-side `AppContext.BaseDirectory` resolver for `engine/`
  side-cars; no host-supplied root for resource resolution.
- Workers move into per-worker subdirs (`Workers/workspace/`,
  `Workers/dotnet/`, `Workers/web/`) to isolate self-contained
  runtimes from each other and from the engine.

**Tests**:
- `scripts/package.ps1 -Local` per RID succeeds.
- A packaged engine binary started inside its staging dir resolves
  every side-car (manifest fixture for each).
- Per-platform VSIX contains the right RID's binaries and no others
  (size + spot-check assertions).
- Corpus byte-equality across RIDs in one build (manifest fixture).

**Out of scope**: marketplace publishing (separate operational
step); existing extension still ships its TS-side instruction
artefacts until Phase 14. Plugin-release and GitHub-release tarball
layout coverage moves with the artefacts themselves — neither is
produced by this repository yet, so neither can be verified here.

## Phase 14 — Extension and hook migration

**Status**: Not started. Gated on Phase 17: six of the engine's seven
analyzer tools fail until it lands (see *Post-migration review*).

| # | Commit subject | State |
|---|---|---|
| 1 | `test(nodejs-core): fail when the engine binary is absent` | TODO |
| 2 | `feat(vscode): spawn and own the engine daemon` | TODO |
| 3 | `refactor(vscode): serve config from the engine` | TODO |
| 4 | `refactor(vscode): serve workspace detection from the engine` | TODO |
| 5 | `refactor(vscode): serve instructions from the engine` | TODO |
| 6 | `refactor(vscode): retire the sideband pipe servers` | TODO |
| 7 | `refactor(vscode): let the engine spawn workers` | TODO |
| 8 | `refactor(vscode): point the mcp server definition at the engine` | TODO |
| 9 | `refactor(vscode): retire the servers manifest` | TODO |
| 10 | `refactor(vscode): drop the chatInstructions contribution` | TODO |
| 11 | `refactor(hooks): serve always-attached instructions from the engine` | TODO |
| 12 | `refactor(hooks): route prompts and tools through the engine` | TODO |
| 13 | `feat(hooks): materialise subagent files under the cache root` | TODO |
| 14 | `refactor(vscode): delete the corpus and manifests, rename resources to assets` | TODO |
| 15 | `test(vscode): migrate suites onto the engine client` | TODO |
| 16 | `docs(plan): mark Phase 14 complete` | TODO |

**Commit grouping.** Row 1 comes first and is not cosmetic: the Phase 12
round-trip suite reads
`const suite = engineBinaryPath === undefined ? describe.skip : describe`,
so it **silently skips** when the .NET half has not been built. It passes
today only because the engine was built locally. Every row below leans on
that suite as its proof that the client works, so the skip becomes a hard
failure before anything is migrated onto it.

Row 2 stands the daemon up and disposes it on deactivate — nothing else can
land first, because the extension has **no** reference to
`EngineDaemonManager` today (verified: zero imports; it consumes
`autocontext-nodejs-core` only for `LogCategory`, `ChannelLogger`,
`LoggerBase`, and the pipe primitives). This phase is a first integration,
not a swap of an existing seam.

Rows 3–5 replace one owned domain each, in dependency order, so a bisect
lands on a single subsystem. Rows 6–9 unwind the spawn/sideband topology:
the pipe servers go first, then worker spawn, then the MCP definition, and
only then the manifest that named them — reversing the order would leave a
consumer without its lookup. Row 10 drops the contribution once
`Instructions.*` serves every reader.

Rows 11–13 migrate the agent-plugin hooks. They live in this phase rather
than one of their own because they are the *second* reader of exactly the
folders rows 3–10 stop reading: splitting them out would mean finishing the
extension migration with the corpus still undeletable, waiting on a separate
phase to close the last two callers. Row 14 is the payoff — with no reader
left on either side, the corpus, the JSON manifests, and the `resources/`
name all go in one commit. Row 15 migrates the suites in one pass because
they cross-cut every preceding row.

**Foundation check (measured 2026-07-28, `features/extension-migration`).**
`EngineDaemonManager` already covers every RPC family this phase needs —
`Config.*` (incl. `subscribeConfig`), `Instructions.*` (all nine, incl.
`SearchByMetadata` and `subscribeInstructions`), `Workspace.*`,
`McpTools.*`, `Discovery.*`, `Agent.*` (five notifications +
`subscribeAgentEvents`), `Logs.*` (incl. both tails), and `Engine.*` with
`subscribeLifecycle`. Find-or-spawn is real
(`engine-connector` / `engine-spawner` / `engine-locator`), and the
round-trip suite exercises it against a genuinely spawned binary. Nothing in
the client blocks this phase.

The 22 modules the deletion list below names all exist — no stale entries —
and total roughly 2,900 of the extension's 7,700 source lines, against 64
test files.

**Goal**: extension becomes a pure `EngineDaemonManager` consumer, and the
agent-plugin hooks with it. The sideband pipe servers and the in-extension
projection/config/corpus classes are deleted. Tree views, decoration
providers, CodeLens, LM tools, and every hook dial the engine over the four
pipes. With the last reader closed, the extension's own corpus and JSON
manifests are deleted and `resources/` becomes `assets/`.

**Design anchors**: `§ Authority model: engine owns, clients cache`,
`§ Projection ownership`, `§ Sharing principle`, `§ LM-tool surface`,
`§ Topology — motivating clients` (agent plugin).

**Code touch — deletions** (from `src/AutoContext.VsCode/src/`):
- `autocontext-config-manager.ts`, `autocontext-config-projector.ts`,
  `autocontext-config-server.ts` — replaced by `Config.*` RPCs.
- `instructions-file-content-projector.ts`,
  `instructions-files-manager.ts`,
  `instructions-files-manifest-loader.ts`,
  `instructions-files-manifest.ts`,
  `instructions-files-metadata-generator.ts`,
  `instructions-files-metadata-loader.ts`,
  `instructions-files-override-watcher.ts`,
  `instructions-file-parser.ts`,
  `instructions-file-sections-cache.ts`,
  `instructions-file-sections-parser.ts` — replaced by
  `Instructions.*` RPCs and the engine-side corpus from Phase 6.
- `log-server.ts`, `health-monitor-server.ts`,
  `worker-control-server.ts` — replaced by engine pipes.
- `workspace-context-detector.ts` — replaced by `Workspace.Detect`.
- `worker-manager.ts` — engine spawns workers itself; the extension
  spawns the engine (and only the engine).
- `servers-manifest-loader.ts`, `servers-manifest.ts`,
  `server-entry.ts` — the extension no longer looks a spawnable entry
  up by id; the engine sits at the fixed bundled `engine/` path.
  `resource-manifest-loader.ts` retires with the last of its three
  subclasses.
- The four LM-tool handler implementations
  (`instructions-files-lm-tools-*`) collapse to thin shims that dial
  `Instructions.*` over `EngineDaemonManager`.

**Code touch — additions/changes**:
- `extension-activation.ts` / `extension-composition.ts`: spawn the
  engine with `--workspace <path> --instance-id <uuid> --idle-timeout
  0 --parent-pid <vscode-pid> --instance-label "vscode (v…); engine
  (v…)"`. The instance UUID is minted once per window.
- Tree views, decoration providers, CodeLens, and hover providers
  all read from `EngineDaemonManager`. `Engine.Lifecycle.Subscribe`,
  `Config.Subscribe`, and `Instructions.Subscribe` drive cache
  invalidation in the UI.
- The MCP server definition (`mcp-server-provider.ts`) repoints to
  `autocontext-engine --mcp-server with-stdio`. Today's
  `--endpoint-suffix` side-channel from the extension's launcher to
  the MCP-host's spawn is replaced wholesale by `--instance-id`.
- `agent-plugin-installer.ts` keeps installing the hook scripts, and
  the hooks themselves migrate in this phase (rows 11–13).

**Code touch — the agent-plugin hooks**:
- Hook scripts move from "carries its own routing scan + corpus
  reader" to "calls `Instructions.GetAlwaysAttached`,
  `Discovery.RouteForPrompt`, `Discovery.RouteForTool`, and fires
  the `Agent.*` notifications". The TS `EngineDaemonManager` from
  Phase 12 is the only seam. SessionStart, UserPromptSubmit,
  PreCompact, and the SubagentStart/Stop pair all land here;
  PreToolUse / PostToolUse / Stop come with them because they share
  the same client and the same RPC families.
- **The hooks are the second reader of the extension's own folders.**
  `src/AutoContext.VsCode/src/hooks/*.cts` resolve `instructions/` and
  `resources/*.json` from the extension root and read them directly at
  runtime — `autocontext-session-start` loads the always-attached
  corpus files (2 sites), `autocontext-user-prompt-submit` reads
  `mcp-tools.json` and `instructions-files.metadata.json` (4 sites).
  Closing these is what makes row 14's deletion possible.
- Sub-agent file materialisation under the per-instance cache root
  (`%LOCALAPPDATA%\autocontext\<workspaceHash>\<instanceId>\cache\subagents\<sessionId>\`,
  POSIX equivalent) lives in the SubagentStart hook; SubagentStop
  cleans it.
- `--instance-id` propagation: hook templates document the
  side-channel the launcher provides (env var inherited from
  launcher); a hook with no resolvable instance-id spawns its own
  engine per the design's *Hook scripts outside a known launcher*
  pitfall.
- The `Engine.Hello`-failure path is a structured hook failure (no
  in-hook disk-read fallback; engine + plugin ship versioned
  together).

**Shipped extension folder — target layout**. The VSIX `extension/`
folder is the contract this phase delivers; Phase 15 removes the
producers once nothing reads the old paths:

| Today | After the engine migration |
|---|---|
| `servers/` (the whole .NET + Node server tree) | **deleted** — the engine owns worker spawn |
| `instructions/` (79 `*.instructions.md`) | **deleted** — served from `engine/Instructions/` |
| `resources/` (4 JSON manifests + 2 images) | renamed **`assets/`**, images only |
| `engine/` | unchanged — the only binary payload |

Constraints measured on `features/extension-migration` (2026-07-28).
Each one gates a deletion that would otherwise look safe:

- **`servers/` is load-bearing until this phase lands.**
  `mcp-server-provider.ts` resolves the MCP binary at
  `join(extensionPath, 'servers', <name>, <name>.exe)`;
  `worker-manager.ts` (`buildSpecs`) spawns all three workers from the
  same tree; `extension-composition.ts` loads `ServersManifestLoader`
  and filters its entries against `mcp-tools.json`; and
  `worker-control-server.ts` maps wire ids from the resulting
  `ServerEntry[]`. Phase 15 cannot delete `servers/` or `servers.json`
  until these four are repointed here.
- **`contributes.chatInstructions` retires — the engine is the only
  reader.** `package.json` contributes 79 entries (two ungated
  always-attached files, 77 gated on
  `autocontext.instructions.<name> && !autocontext.override.<name>`).
  That contribution has **VS Code** open the `.md` itself, so the
  corpus has a second reader that never passes through the engine.
  Repointing the paths at `./engine/Instructions/` would work — the
  basenames match the engine corpus exactly, 79 for 79 — but it would
  preserve the divergence, so it is **not** the target. The gate is
  boolean and whole-file, while `.autocontext.json` supports
  `disabledRules` and `InstructionsBodyProjector` filters them out of
  the served body: a user who disables one rule still receives it
  through `chatInstructions` today. Always-attached injection belongs
  to `Instructions.GetAlwaysAttached` via the SessionStart hook,
  per-prompt routing to `Discovery.RouteForPrompt`, and browsing to
  the LM tools — all engine-served, all honouring rule-level state.
  Dropping the contribution is what makes `instructions/` deletable.
- **The two corpora are not a clean duplicate.**
  `src/AutoContext.VsCode/instructions/` and
  `src/AutoContext.Engine/Instructions/` both hold 79 identically-named
  files, but the first is CRLF and the second LF. After normalising line
  endings 73 of 79 match; six differ in content and need reconciling
  before the VsCode copy is deleted — `copilot`, `design-principles`,
  `dotnet-coding-standards`, `dotnet-testing`, `testing`,
  `web-testing`. Phase 16 later corrects the tool names and parameters
  in 48 engine files and deliberately leaves the VsCode copy on the
  legacy server's names; for those `## MCP Tool Validation` and
  `## Workflow MCP Tools Triggers` sections the engine text wins.
- **`resources/` splits cleanly.** `instructions-files.json`,
  `instructions-files.metadata.json`, `mcp-tools.json`, and the copied
  `servers.json` are each superseded by an engine-side equivalent;
  `logo.png` and `logo_vscode.svg` are the only genuine assets. The
  rename touches `package.json`, which points its activity-bar icon at
  `resources/logo_vscode.svg`.

**Definition of done — no second reader survives.** The engine owns the
corpus, the manifests, and worker identity, so after this phase nothing
else reads them. Deleting `instructions/` and the `resources/*.json`
manifests is the *consequence* of closing these readers, not the goal:
a folder that still has a reader cannot be removed, and a reader that
outlives its folder is a defect even when its output happens to be
correct today. Every row below was counted on
`features/extension-migration` (2026-07-28):

| Reader today | Sites | Replacement |
|---|---|---|
| TS modules resolving `join(extensionPath, 'instructions' \| 'resources', …)` | 13 across 9 files | `Instructions.*` / `Config.*` RPCs |
| `contributes.chatInstructions` — VS Code opens the `.md` itself | 79 entries | SessionStart hook, `Discovery.RouteForPrompt`, LM tools |
| `ServersManifestLoader` → MCP binary + worker spawn paths | 4 modules | fixed bundled `engine/` path |
| `.github/instructions/` override precedence in `instructions-file-content-projector.ts` | 1 | `Instructions.GetRaw` with `source: bundled \| override \| active` |

The override row is the subtle one: the engine already resolves
override-over-bundled in `InstructionsOverridesWatcher` +
`InstructionsBodyProjector`, so today two implementations of the same
precedence rule run side by side. They are not obliged to agree, and
`disabledRules` already shows what divergence looks like in practice.

The phase is done when a search of `src/AutoContext.VsCode/src/` for
`join(extensionPath, 'instructions'` and `join(extensionPath,
'resources'` returns nothing and `contributes.chatInstructions` is
absent from `package.json`. The folders themselves survive the extension rows:
the hooks are still reading them (6 sites), so the deletion and the
`resources/` → `assets/` rename land in row 14 of this phase, once the
last reader closes.

**Tests**:
- Extension Vitest suites: every replaced module's test coverage
  migrates onto `EngineDaemonManager` fakes / engine-in-process fixtures.
  No coverage drops below the replaced module's bar.
- `scripts/test.ps1 -Smoke` (the VS Code extension smoke test) runs
  end-to-end: extension activates, spawns the engine, tree view
  populates, an instruction toggle round-trips.
- Cross-window scenario: two VS Code windows on the same workspace
  spawn two engines; toggles in one window reach the other through
  the cross-instance `.autocontext.json` path (Phase 3 contract).
- Per-hook fixture-based tests against a spawned engine.
- Side-channel UUID inheritance: hook with env var reaches the
  launcher's engine; hook without spawns its own.
- Sub-agent cache materialisation + cleanup.
- `Engine.Hello` mismatch surfaces as a structured hook error.
- A packaged VSIX carries no `instructions/` and no `resources/*.json`.

**Out of scope**: `Mcp.Server` deletion and the `servers/` teardown
(Phase 15); any host-specific hook-host detection (the design says
hooks are host-agnostic — Claude Code, VS Code Copilot, future hosts).

## Phase 15 — `AutoContext.Mcp.Server` retirement

**Status**: Not started.

**Goal**: the standalone MCP-server project is gone, and with it the
whole `servers/` distribution path. Phase 11 left the project
**untouched** as the legacy stdio server (no delegating shim), so this
phase deletes it as-is once its last consumers have flipped to the
engine binary. `engine/` becomes the only binary payload a shipped
artefact carries. Tests fold into `AutoContext.Engine.Core.Tests`.

**Design anchors**: `§ What the engine absorbs from today's topology`,
`§ Test-project layout`, `§ Distributed bundle layout`.

**Code touch**:
- Delete `src/AutoContext.Mcp.Server/` and
  `tests/AutoContext.Mcp.Server.Tests/` — the legacy server, untouched
  since it was superseded in Phase 11, retires here with no shim to
  unwind.
- Tests worth keeping move into `AutoContext.Engine.Core.Tests`
  (the schema-validation tests, the manifest-loader tests, the
  envelope-composition tests).
- **Rehome the Node worker build.** `Build-EngineBundle` and
  `Copy-EngineToExtension` currently stage `Workers/web/` by copying
  out of `servers/AutoContext.Worker.Web`, which the Node pipeline
  produced first — so `servers/` is a live *input* to the engine
  bundle, not merely duplicated output. The Node worker is
  RID-independent, so it bundles once into a single staging directory
  that every per-RID copy draws from. This lands **before** anything
  under `servers/` is deleted.
- **Retire `servers.json` and the `servers/` tree.** Once
  `mcp-server` is gone, the manifest's remaining entries (`workspace`,
  `dotnet`, `web`) are workers already declared by their own
  `.autocontext-worker.json` descriptors and rostered in generated
  `workers.json` — pure duplication of the descriptor-driven roster.
  Removing it retires `Build-DotNetPackage`,
  `Copy-DotNetToServersFolder`, `Copy-NodeJsToServersFolder`, the
  `ServersDir` / `NodeServers` / `DotnetServers` /
  `ServerProjectPaths` context fields, and the `resources/servers.json`
  asset copy. **Gated on Phase 14**: four extension modules still
  resolve the MCP binary and every worker out of `servers/` (see that
  phase's *Shipped extension folder* contract), so this deletion is
  only safe once they dial the engine.
- TS-side `servers-manifest-loader.ts`, `servers-manifest.ts`, and
  `server-entry.ts` retire with the manifest; the extension resolves
  the engine at the fixed bundled `engine/` path instead of looking up
  a spawnable entry.
- The engine-owned `mcp-tools-registry.json` was authored fresh
  under `AutoContext.Engine/Resources/` in Phase 7 (not moved — the
  old `mcp-workers-registry.json` stayed in `AutoContext.Mcp.Server/`
  untouched under its legacy name); deleting `AutoContext.Mcp.Server/`
  here removes that legacy registry and schema along with the rest of
  the project.
- Solution file (`AutoContext.slnx`) cleaned of the retired project.
- `build.ps1` no longer references `AutoContext.Mcp.Server`.

**Tests**:
- Full solution build + test + smoke green.
- A spawned `autocontext-engine --mcp-server with-stdio` answers
  every `tools/list` and `tools/call` the old `Mcp.Server` answered
  (regression fixture set lifted from `AutoContext.Mcp.Server.Tests`).
- The engine bundle smoke suite still resolves every side-car with
  the Node worker staged from its new home.
- A packaged VSIX carries `engine/` and no `servers/`.

**Out of scope**: any further surface work; the engine has shipped.

## Phase 16 — Corpus-to-registry tool contract

**Status**: In progress on branch `features/corpus-tool-contract`.

| # | Commit subject | State |
|---|---|---|
| 1 | `fix(instructions): name the registered tools and their parameters` | TODO |
| 2 | `feat(instructions-manifest-gen): validate tool obligations against the registry` | TODO |
| 3 | `docs(plan): mark Phase 16 complete` | TODO |

**Commit grouping.** Row 1 corrects the corpus before row 2 makes the
corpus's claims machine-checked, because the validator would fail the
build on the text as it stands today. Landing them in the other order
would leave a red commit on the branch, against the *every commit
compiles green* rule. Row 2 carries the registry reader, the
validator, the generator's new input, the `.targets` wiring, and the
tests in one commit for the same reason Phase 10 grouped its DTOs,
stream, and handler: a reader with no validator, or a validator with
no caller, is an intermediate state that proves nothing on its own.

**Measured on `features/extension-migration` (2026-10-08).** The
corpus names three MCP tools the registry does not have:
`analyze_csharp_code` (32 files), `analyze_typescript_code` (16
files), and `read_editorconfig` (`copilot.instructions.md`). The
registered names are `analyze_csharp_code_style`,
`analyze_typescript_code_style`, and `read_editorconfig_rules`. The
C# sections tell the agent to pass `originalPath`, `originalNamespace`,
and `comparedPath`; the registered parameters are `filePath`,
`projectDirectory`, and `rootNamespace`. The TypeScript sections tell
it to pass `originalPath`; the tool takes `content` alone. Two
registered C# tools — `analyze_csharp_project_structure` and
`analyze_csharp_testing_style` — are named by no file at all, so the
agent is never told they exist. Nothing in the build notices any of
this: cross-file `[locator#fragment]` references are validated, tool
references are not.

**The VS Code corpus copy is not touched.** The drift is in the
*engine* corpus only. The copy under
`src/AutoContext.VsCode/instructions/` is served next to the legacy
`AutoContext.Mcp.Server`, whose `mcp-workers-registry.json` really does
register `analyze_csharp_code` (`content`, `originalPath`,
`comparedPath`, `projectDirectory`, `rootNamespace`),
`analyze_typescript_code` (`content`, `originalPath`), and
`read_editorconfig` (`path`). That text is correct for the server the
shipping extension launches. The engine corpus was copied from it in
Phase 5, and the engine registry renamed the tools in Phase 7 without
the copy following. Renaming the extension copy would send the
shipping extension's agent to tools its server does not have. The
extension copy therefore keeps the legacy names until Phase 14 deletes
it; the 48 files that now differ between the two copies differ by
design, and when Phase 14 reconciles the corpora the engine copy's
tool sections win.

**Goal**: every tool obligation an instruction file states is a
claim the build proves against `mcp-tools-registry.json`. A file
that names an unregistered tool, or a parameter the named tool does
not take, fails `instructions-manifest-gen` with the same file/line
locator a dangling rule reference produces. The corpus is corrected
to the registered names and parameters, and the C# sections name all
three C# tools with the inputs each one takes.

**The contract.** Inside a section headed `MCP Tool Validation` or
`Workflow MCP Tools Triggers`, every inline-code token is classified
by shape:

- *Tool-name shape* — lowercase `snake_case` with at least one
  underscore (`analyze_csharp_code_style`). It must equal a
  `tools[].name` in the registry.
- *Parameter shape* — a lowercase-initial identifier with no
  underscore (`content`, `filePath`). It must be a declared
  parameter of a tool named in the **same** section; a parameter of
  a tool named elsewhere does not count, because the reader of that
  section has no way to know which tool it belongs to.
- Anything else in inline code — file extensions (`.cs`), commands
  with spaces (`git status`), element names (`PackageReference`) —
  is outside the contract by shape and never reported.

Fenced code blocks inside those sections are skipped, as the
reference parser already skips them. Sections under any other
heading are not scanned: the always-attached files name the LM
discovery tools (`list_autocontext_instructions_files`, …) in prose,
and those are served from code, not from the registry.

**Design anchors**: `§ Resource manifests` (the generator reads the
registry as a validation input), `§ Pitfalls — Corpus drift against
the registry`, `§ P3` (the registry stays the single statement of
each tool's model-facing contract; the corpus may cite it, never
restate it).

**Code touch**:
- `src/AutoContext.Engine/Instructions/` only (see *The VS Code
  corpus copy is not touched* above) — the `## MCP Tool
  Validation` sections of the 29 files sharing the C# block, the 15
  sharing the TypeScript block, and the per-file variants
  (`code-review`, `dotnet-aspx`, `dotnet-xaml`) are rewritten to the
  registered names and parameters; the `## Workflow MCP Tools
  Triggers` table in `copilot.instructions.md` names
  `read_editorconfig_rules`. No rule text changes, no file version
  changes.
- `AutoContext.Instructions.Manifest.Generator/`:
  `McpToolsRegistryReader` (+ interface) reads `tools[].name` and
  each tool's `parameters` keys from the registry — a minimal reader
  over `JsonDocument`, because the generator cannot reference
  `Engine.Core` (it runs before the engine compiles) and needs
  nothing else from the file; `InstructionsToolObligationValidator`
  (+ interface) scans the contracted sections of every parsed file
  and yields one `InstructionsFileToolObligationFindingEntry` per
  fault (`UnknownTool` | `UnknownParameter`, with the token and its
  body line); `InstructionsManifestGenerator` takes the registry
  path as a third positional argument
  (`[corpus-directory, catalog-json-path, registry-json-path,
  manifest-json-path]` — inputs before the output), logs each
  finding as an error, and exits `1` when any exist;
  `InstructionsManifestGenerator.targets` passes
  `Resources/mcp-tools-registry.json`.
- `AutoContext.Engine.Tests.Support/Diagnostics/EngineResourcesPath`
  — the sibling of `EngineInstructionsPath` for the shipped
  `Resources/` directory, so the round-trip suite can validate the
  shipped corpus against the shipped registry.

**Tests**:
- Registry reader: names and parameter sets round-trip; a tool with
  no `parameters` yields an empty set; a missing or malformed file
  is a curatorial fault with the `[mcp-tools-registry.json]` locator.
- Validator: an unregistered tool name is flagged; a parameter the
  section's tool does not declare is flagged; a parameter of a tool
  named only in another section is flagged; out-of-shape tokens are
  ignored; a fenced block inside the section is ignored; the same
  token under an uncontracted heading is ignored; a clean section
  yields nothing; findings carry file, line, and token.
- Shipped corpus round-trip: the real corpus validates clean against
  the real registry, which is the regression that catches the next
  rename.

**Out of scope**: emitting each file's obligations into
`instructions-manifest.json` — Phase 19 is its first reader, so
Phase 19 adds it (*just-in-time scaffolding*); validating the LM
discovery tools named outside the contracted sections; any change to
the registry's own schema.

## Post-migration review (2026-10-08)

Phases 17–21 were re-planned after measuring the premises the first
draft rested on. Three findings reorder everything after Phase 16:

1. **Six of the engine's seven analyzer tools cannot run.** The engine
   sends the registered tool name to the worker as the task name
   (`McpToolsInvoker.BuildRequestBytes`), but the workers register their
   tasks under different names (`analyze_csharp_coding_style`,
   `analyze_csharp_test_style`, `analyze_nuget_hygiene`,
   `analyze_git_commit_format` + `_content`, `get_editorconfig_rules`,
   `analyze_typescript_coding_style`), and the worker dispatcher answers
   `Unknown task` for anything else. Only
   `analyze_csharp_project_structure` works, because its two names
   happen to match. The legacy server fanned each tool out to its tasks;
   the engine registry dropped that mapping in Phase 7. Verified by
   driving a staged engine over MCP stdio and calling every tool. No
   test caught it: the end-to-end suites call a test-driver echo tool,
   never a shipped one.
2. **The checks are too noisy to run unattended.** Running the real
   tasks over this repository (which builds clean under its own strict
   analyzers) and over the stock `dotnet new` templates flagged 9 of 40
   source files for coding style and 11 of 40 for member ordering, 10 of
   15 test files for nullable use and 9 of 15 for test style, and 3 of
   the 6 template files for coding style alone. The top hits are taste or context errors, not
   bugs: the null-forgiving operator in tests, `Assert.Multiple` for any
   two asserts, XML docs on public members of internal types, a
   mandatory blank line before control flow, alphabetical member order,
   expression-body arrow placement, `ConfigureAwait` in applications,
   and TypeScript rules applied to plain JavaScript (the TS analyzer
   parses `.js`/`.vue`/`.svelte` content as TypeScript). Every check
   also reports the whole file, so a one-line edit inherits all of the
   file's existing debt — against `copilot.instructions.md`'s own *don't
   retrofit* rule.
3. **Several first-draft premises were false.** Worker reports carry no
   rule ids (the `[async-await INST0007]` tags are source comments), so
   neither disabled rules nor per-rule filtering can be applied to
   them; ConfigureAwait is not the only rule that ignores project
   facts; the VS Code extension gives little without Copilot, because
   Copilot's chat is what reads everything it contributes; the
   always-attached text names VS Code LM-tool names
   (`list_autocontext_instructions_files`, …) that the engine's MCP
   server serves as `list_instructions`, `get_instructions`, …, so a
   Claude Code agent is pointed at tools that do not exist; and the
   prompt hook tells the agent today that "an automatic post-write hook
   will remind you", while `hooks.json` registers no such hook.

The phases below put correctness before reach and accuracy before
automation. The exception is Claude Code support (Phase 20): its
first two parts need neither the engine nor the checks, so they can
ship before everything else here. The first draft's *workspace-authored instruction files*
and *session usage report* are deferred at the end with the reasons.

## Phase 17 — Make the checks callable and the guidance true

**Status**: Not started. Gates Phase 14: the extension must not move
onto an engine whose tools fail.

| # | Commit subject | State |
|---|---|---|
| 1 | `test(engine): call every registered tool through the bundle` | TODO |
| 2 | `fix(engine): fan each tool out to its worker tasks` | TODO |
| 3 | `fix(hooks): stop promising a post-write hook` | TODO |
| 4 | `docs(plan): mark Phase 17 complete` | TODO |

**Commit grouping.** Row 1 lands first. Because no commit may be red,
it asserts on the one shipped tool that already works
(`analyze_csharp_project_structure`) and carries a skip list naming the
six that fail, each with the reason; row 2 empties the skip list. Row 3 is
independent and fixes text the shipping extension shows today.

**Goal**: every tool the engine lists over MCP and `McpTools.List`
returns a real report when called with valid arguments, and a test
proves it for every registry entry, so a future rename on either side
fails CI instead of the agent.

**Code touch**:
- `mcp-tools-registry.json` (+ schema): each tool declares the worker
  task names it runs (`tasks: [...]`), mirroring what the legacy
  `mcp-workers-registry.json` nested. A tool whose name is its single
  task may omit the list.
- `McpToolsInvoker`: invokes each task in order on the same worker,
  merges the `{ passed, report }` outputs into one result (passed only
  if all passed; reports concatenated under the task's heading), and
  returns the first task error as the tool error.
- Engine bundle smoke (`AutoContext.Engine.Tests`): for every registry
  tool, a fixture argument set and an assertion that the result is the
  `ok` arm. Fixtures live beside the test, one per tool.
- `autocontext-user-prompt-submit.cts`: delete the post-write promise.
  Phase 19 adds the hook and may restore a truthful sentence then.

**Tests**: the bundle smoke above; invoker unit tests for fan-out,
merge, first-error, and the single-task default.

**Out of scope**: changing any check's rules (Phase 18).

## Phase 18 — Check accuracy and structured findings

**Status**: Not started. Independent of Phase 14; gates Phase 19.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(workers-core): report structured findings` | TODO |
| 2 | `test(worker-dotnet): pin the noise baseline` | TODO |
| 3 | `fix(worker-dotnet): judge C# by the project's own facts` | TODO |
| 4 | `fix(worker-web): stop applying TypeScript rules to JavaScript` | TODO |
| 5 | `feat(engine): drop findings for disabled rules` | TODO |
| 6 | `docs(plan): mark Phase 18 complete` | TODO |

**Why this phase exists.** Phase 19 runs these checks on every edit.
A check that flags correct code makes the agent rewrite correct code,
which is worse than no check. The 2026-10-08 measurement above is the
baseline this phase must beat.

**Goal**:
- Every finding is structured: `{ ruleId, file?, line, severity,
  message }`, with `ruleId` the `<file-key>#INST####` the finding
  enforces. The text report is rendered from it, so agents still read
  prose.
- Rules defer to what the project already says: `.editorconfig`
  naming and style keys, the project's SDK and `OutputType` (scoping
  ConfigureAwait to libraries), test-project detection (the
  null-forgiving operator and multi-assert rules do not apply to test
  code), and the containing type's visibility (no XML-doc demand on
  members of internal types).
- Rules that are taste rather than correctness ship at `severity:
  suggestion` and never gate: blank line before control flow,
  expression-body arrow placement, alphabetical member order,
  `Assert.Multiple`, `Should_` test names.
- The TypeScript analyzer parses by file kind and skips
  annotation-only rules for JavaScript.
- `McpTools.Invoke` drops findings whose `ruleId` the workspace has
  disabled — the first point at which `disabledRules` reaches a tool,
  as `architecture.md` § *Disable is granular* requires.

**Acceptance**: rerun the measurement (this repository's `src/` and
`tests/` samples plus the `webapi`, `worker`, `classlib`, `xunit` and
`console` templates). No gating finding on any template file; gating
findings on this repository only where a human reviewer agrees the code
is wrong. The fixture set and the measurement script live in the
worker test projects so the baseline is re-checkable.

**Out of scope**: new rules.

## Phase 19 — Verification hooks

**Status**: Not started. Gated on Phases 14, 16, 17 and 18.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(instructions-manifest-gen): emit each file's tool obligations` | TODO |
| 2 | `feat(engine-core): serve Discovery.RouteForFile` | TODO |
| 3 | `feat(engine-core): hold the per-session touched-file set` | TODO |
| 4 | `feat(hooks): report the violations an edit introduces` | TODO |
| 5 | `feat(hooks): summarise outstanding violations at turn end` | TODO |
| 6 | `refactor(instructions): stop mandating per-turn discovery calls` | TODO |
| 7 | `docs(plan): mark Phase 19 complete` | TODO |

**Why this phase exists.** The instruction files *ask* the agent to run
its checks; the agent skips them when the edit looks small or the
context was compacted. Moving the obligation into the hook makes the
checks run. With Phases 17 and 18 done, what they report is worth
acting on.

**Goal**: after a write, the agent sees the violations *its edit
introduced*, in the same turn, and nothing else.

**Code touch**:
- Manifest per-file `mcpTools` and `Discovery.RouteForFile(path)` as
  the design describes; tool selection is per file, not a union — a
  tool an instruction file scopes to test files runs only on test
  files.
- **Change-only reporting.** `PreToolUse` on a write tool captures the
  file's pre-edit content (the session cache root, as sub-agent files
  do); `PostToolUse` runs the obliged checks on both versions and
  reports only findings whose `(ruleId, message)` is absent before the
  edit. A new file has every finding new. Requires Phase 18's
  structured findings.
- `Stop` is **advisory by default**: it re-checks the touched files and
  emits the outstanding introduced violations as context. Blocking the
  turn is a per-workspace opt-in in `.autocontext.json`, off by
  default.
- Once routing is per file in the hook, `autocontext.instructions.md`
  stops requiring `list`/`get` calls for every file on every turn
  (tool calls the agent pays for in latency and context); discovery
  stays available on demand.

**Tests**: hook fixtures against a spawned engine — an edit introducing
`async void` reports exactly that finding; an edit to a file with
existing violations reports none of them; a `.js` edit runs no
TypeScript-only rule; a test-file edit runs the test-style check and a
non-test edit does not; `Stop` is advisory unless opted in.

**Out of scope**: whole-workspace audits (a CLI verb); auto-fixing.

## Phase 20 — Claude Code support

The first draft treated Claude Code as one late phase, gated on the
end of the engine migration. That left Claude Code users with nothing
until Phase 15, although most of what they need does not depend on the
engine at all. Claude Code natively reads path-scoped rule files —
`.claude/rules/*.md` whose `paths:` frontmatter holds globs, loaded
when Claude touches a matching file — which is the same model as
AutoContext's `applyTo` instruction files. It also loads plugin skills
on demand by description, ships plugins that bundle hooks and MCP
servers, and runs the hooks format AutoContext's plugin already uses.
What it does **not** document is a way to ship per-platform native
binaries with a plugin.

So the work splits by dependency. 20A ships on today's extension with
no engine. 20B is text and can land any time. 20C needs the engine's
checks to work (Phase 17) and the hooks to dial the engine (Phase 14,
rows 11–13), but not the end of the migration.

Checked in code 2026-10-08: every surface the extension contributes
(`contributes.chatInstructions`, `vscode.lm.registerTool`, the MCP
server definition provider, `chat.pluginLocations`) is read by Copilot
only, so a Claude Code session — even inside VS Code with the extension
installed — receives no AutoContext instructions and no AutoContext
tools.

### Phase 20A — Export to Claude Code rules

**Status**: Not started. Independent; ships on the current extension.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(vscode): export instructions as Claude Code rules` | TODO |
| 2 | `feat(vscode): export workflow guidance as Claude Code skills` | TODO |
| 3 | `docs(vscode): document the Claude Code export` | TODO |
| 4 | `docs(plan): mark Phase 20A complete` | TODO |

**Why this phase exists.** It is the largest gain for the least work:
the files exist, the user's disabled rules are already applied to
them, and Claude Code already knows how to load them only when they
are relevant — at no context cost until a matching file is touched.

**Goal**: from the Instructions view's export action, a user picks
*Claude Code* as the target and gets one rule file per selected
instruction file, scoped to the same files its `applyTo` names.

**Code touch**:
- `instructions-files-exporter.ts` gains a target choice
  (*Copilot* — today's `.github/instructions/` — or *Claude Code*).
  The Claude Code target reads the same per-window generated copy the
  Copilot export reads (disabled rules removed, rule ids stripped,
  frontmatter kept) and rewrites the frontmatter: `applyTo` becomes a
  `paths:` list, one entry per comma-separated glob (brace groups kept
  if `paths:` accepts them, otherwise expanded — the extension's own
  `applyTo` handling only comma-splits, so expansion is new code that
  mirrors the engine's `applyTo` parser); `name` and `description` are kept; a
  generated-by marker records the source file, its version, and its
  content hash so a later sync can tell a stale or hand-edited copy
  from a current one.
- Files with no `applyTo` — the workflow guidance (`code-review`,
  `git-commit`, `design-principles`, `rest-api-design`; `testing` is
  scoped to test files and exports as a rule) —
  would load unconditionally as rules. They export instead as skills
  (`.claude/skills/<key>/SKILL.md`), whose existing `description`
  ("Apply when drafting a git commit message…") is exactly what a skill
  needs to be picked on demand.
- The always-attached files (`copilot`, `autocontext`) are **not**
  exported: their text is Copilot-specific until 20B.
- `## MCP Tool Validation` sections are dropped from the exported
  copy. They name the legacy server's tools, which a Claude Code
  session cannot call; pointing the agent at missing tools is the
  failure Phase 16 exists to prevent. 20C restores them once the
  engine serves Claude Code.
- An exported Claude Code file is a snapshot, not an override: the
  overrides watcher does not read `.claude/`, so AutoContext's toggles
  do not reach it after export. Re-exporting updates it; the marker
  lets the export warn before overwriting a hand-edited copy.

**Pre-flight decisions (check the Claude Code memory and skills docs at
phase open)**: whether `paths:` takes a list and brace globs (if not,
one glob per entry after expansion); whether rule files in a
subdirectory (`.claude/rules/autocontext/`) are read (if not, a flat
`autocontext-<key>.md` prefix); and whether project skills live under
`.claude/skills/`.

**Tests**: frontmatter rewrite fixtures for single globs, comma lists,
and brace groups; a disabled rule is absent from the export; tool
sections are dropped; workflow files land as skills; always-attached
files are skipped; the overwrite prompt fires on a hand-edited copy.

**Out of scope**: automatic re-export on toggle (a later sync
feature); anything that needs the engine.

### Phase 20B — Host-neutral always-attached guidance

**Status**: Not started. Independent.

| # | Commit subject | State |
|---|---|---|
| 1 | `fix(instructions): name discovery tools every host serves` | TODO |
| 2 | `refactor(instructions): split host policy from shared guidance` | TODO |
| 3 | `docs(plan): mark Phase 20B complete` | TODO |

**Why this phase exists.** The two always-attached files are written
for Copilot. `autocontext.instructions.md` names the VS Code LM-tool
names (`list_autocontext_instructions_files`, …), while the engine's MCP
server — the only discovery surface Claude Code can reach — serves
`list_instructions`, `get_instructions`, `search_instructions_by_*`.
`copilot.instructions.md` mixes guidance any agent needs (instruction
precedence, how to discover rules) with one host's policy.

**Goal**: one always-attached text that is true on every host, plus
host policy that is clearly separate (and, per Phase 21, opt-in).

**Code touch**:
- The engine serves the discovery tools under one name set on every
  surface; the VS Code LM-tool registrations are renamed to the MCP
  names, so the text can name them once. Phase 16's validator extends
  to the discovery-tool section once there is a single name set.
- `copilot.instructions.md` splits into a host-neutral always-attached
  file and a Copilot-specific one; the neutral file is what 20A and 20C
  may hand to Claude Code.

**Tests**: the validator covers the discovery-tool names; a fixture
proves both hosts' tool lists contain every name the neutral text
cites.

### Phase 20C — Claude Code plugin with the engine

**Status**: Not started. Gated on Phase 17 (callable checks) and Phase
14 rows 11–13 (hooks dial the engine). The standalone release row is
additionally gated on Phase 15.

| # | Commit subject | State |
|---|---|---|
| 1 | `feat(plugin): declare the engine's MCP server` | TODO |
| 2 | `feat(vscode): register the plugin with Claude Code` | TODO |
| 3 | `build(scripts): release the plugin with its engine` | TODO |
| 4 | `docs: install AutoContext for Claude Code` | TODO |
| 5 | `docs(plan): mark Phase 20C complete` | TODO |

**Goal**: a Claude Code session gets the same instructions, discovery
tools, checks, and (after Phase 19) verification a Copilot session
gets.

**Code touch**:
- `plugin.json` declares the engine in its MCP-server role
  (`--workspace` from the project directory, `--mcp-server
  with-stdio`), its command resolved against `${CLAUDE_PLUGIN_ROOT}`.
- **Inside VS Code** (row 2): the extension already ships the engine
  per platform, so it registers its plugin directory with Claude Code
  the way it registers it with Copilot through `chat.pluginLocations`;
  no download is involved. This is the path for users like this
  repository's maintainer, who run Claude Code in VS Code.
- **Standalone** (row 3, after Phase 15): a release asset per platform
  carries `plugin/` plus that platform's `engine/`. Because plugins
  have no documented binary channel, the plugin's first SessionStart
  fetches and verifies the platform's engine into the plugin's data
  directory unless one is already present.
- With the engine reachable, the 20A export keeps the tool-validation
  sections (now naming engine tools), and the hooks serve
  always-attached and routed instructions directly.
- The extension README states that its in-editor agent features need
  either Copilot Chat or Claude Code, and how to enable each.

**Pre-flight decisions**: how Claude Code registers a local plugin
directory non-interactively (for row 2), and whether a hook may
download into the plugin data directory on first run (for row 3) —
both from the plugin docs at phase open.

**Tests**: a packaged plugin starts its engine and answers `tools/list`
with the discovery tools the neutral text names; the Phase 17 bundle
smoke runs against the plugin layout; the extension's registration is
idempotent and removed on uninstall.

## Phase 21 — Context cost

**Status**: Not started. Measurement row is independent; the
reduction rows follow Phases 14 and 19.

| # | Commit subject | State |
|---|---|---|
| 1 | `test(hooks): measure the context AutoContext injects` | TODO |
| 2 | `fix(hooks): inject the always-attached files once` | TODO |
| 3 | `refactor(instructions): make host policy opt-in` | TODO |
| 4 | `docs(plan): mark Phase 21 complete` | TODO |

**Why this phase exists.** Every token AutoContext adds displaces the
user's own code from the agent's context. Measured 2026-10-08: the two
always-attached files are about 10 KB, injected twice in VS Code (once
by `contributes.chatInstructions`, once by the SessionStart hook), plus
a discovery block on every prompt. Nothing measures or bounds this.
`copilot.instructions.md` also imposes one maintainer's working
policy on every user — `pwsh` commands, *act immediately*, git
restrictions — and declares itself senior to the user's own guidance.

**Goal**: a budget test fails when the injected context grows past an
agreed size; each always-attached file reaches the agent once; host
policy that is preference rather than safety moves behind a
`.autocontext.json` opt-in.

**Tests**: the budget test; a SessionStart + prompt fixture counts one
copy of each always-attached file.

## Deferred

- **Workspace-authored instruction files.** The premise holds — the
  overrides watcher inventories new-name files but
  `InstructionsListProjector.Project` only iterates manifest entries, so
  they are never served. The value does not: the default override root
  `.github/instructions/` is a folder VS Code Copilot already reads
  natively with `applyTo`, Claude Code has its own project rules, and
  `copilot.instructions.md` ranks itself above workspace guidance. What
  the phase would add — rule-level disable and search over team files —
  is small next to Phases 17–21. Revisit after Phase 21, together with
  the precedence rule.
- **Session usage report.** Its purpose was curation data, which needs
  history across sessions, and the design keeps the tally in memory for
  one engine lifetime. It serves the maintainer, not the user. Phase
  21's measurement covers the user-facing cost question; a curation
  report can return when there is a design for retaining data.

## Cross-phase concerns

### Risk and ordering

- **Phase 3 (config) and Phase 6 (instructions runtime) are the
  highest-risk phases.** Reload coalescing and snapshot immutability
  are subtle; both ship with the heaviest test budget.
- **Phase 13 (distribution) cannot ship before Phase 11
  (MCP-server-only role).** The shipped binary needs to support both
  roles before any host bundle includes it.
- **Phase 14 (extension and hooks) cannot ship before Phases 6, 7, 9,
  12.** Both the extension and the hooks consume every one of those
  surfaces, and both dial through the Phase 12 TS client.
- **Phase 15 (Mcp.Server retirement) is the last migration phase** so
  the regression surface stays observable until everything else has
  flipped. Phases 16–21 are post-migration surface work: they change
  what AutoContext does for a user, not how the engine is built, and
  they are ordered by what they depend on rather than by number.
- **Phase 17 (callable checks) gates Phase 14.** Six of the engine's
  seven analyzer tools fail with `Unknown task` today; migrating the
  extension onto the engine before they are fixed would ship an
  extension whose checks do not run. Row 3 of Phase 17 (the false
  post-write promise) is independent and can land immediately.
- **Phase 18 (check accuracy) depends on nothing above** and can land
  before Phase 14. It gates Phase 19: automating checks that flag
  correct code makes the agent worse.
- **Phase 19 (verification hooks) cannot ship before Phases 14, 16, 17
  and 18.** The hooks dial the engine only after Phase 14; the
  obligations are trustworthy after 16, callable after 17, and worth
  acting on after 18, whose structured findings change-only reporting
  needs.
- **Phase 20A (Claude Code rules export) and 20B (host-neutral
  guidance) depend on nothing above** and can land now, on the current
  extension. **20C (Claude Code plugin with the engine)** needs Phase 17
  and Phase 14 rows 11–13; only its standalone release row waits for
  Phase 15, because a plugin release that carries `engine/` must be the
  only binary payload and `servers/` is still an input to the bundle
  until then.
- **Phase 21 (context cost)**: the measurement row is independent; the
  single-injection row follows Phase 14 (which drops
  `chatInstructions`), and the per-turn discovery reduction lives in
  Phase 19.

- **The shipped extension folder empties in 14 → 15 order, and not
  before.** Measured on `features/extension-migration` (2026-07-28):
  `servers/` is still how the extension resolves the MCP binary and
  every worker, `instructions/` still backs 79
  `contributes.chatInstructions` entries that VS Code reads from disk,
  and both the hooks and the TS loaders still read `resources/*.json`.
  Retiring any of those folders ahead of the phase that repoints its
  readers leaves the extension with no MCP server, no workers, or no
  instruction contributions. Phase 14 closes every reader — extension
  and hooks alike — and deletes the folders in its final rows; Phase 15
  then removes the producers.

### What every phase explicitly does *not* do

- No version bumps. `version.json`, `package.json` `version` fields,
  and `.csproj` `<VersionPrefix>` stay where they are until the user
  asks (see `copilot-instructions.md` § Versioning).
- No instruction-corpus content edits unless the phase explicitly
  rewrites a section the engine is replacing, or — Phases 16 and 18
  — the section states a claim the phase makes machine-checked (tool
  names and parameters; a rule's enforcement scope). Engine
  consolidation does not mean rewriting curated guidance.
- No "improvements" beyond the phase scope. Refactors, style
  sweeps, doc cleanups, or unrelated bug fixes wait for their own
  change (see `copilot-instructions.md` § Implementation Discipline).
- No new portability abstractions (`IFileSystem`, `IWorkspace`,
  …) — see `design § Sharing principle` and its pitfall entry.

### Resolved pre-flight decisions

1. **Phase 4 flag table — port the existing TS tables verbatim.**
   The authoritative rule set already lives declaratively in
   [`workspace-context-detector.ts`](../src/AutoContext.VsCode/src/workspace-context-detector.ts)
   as four `as const` arrays: `fileRules` (file-glob flags),
   `npmContentRules` (`package.json` regex flags),
   `dotnetContentRules` (`.csproj` regex flags), and
   `flagActivationRules` (`[child, parent]` transitive activations).
   Phase 4 ports the flag set unchanged — same flag names, same
   globs, same regex patterns, same activation edges — but folds the
   two identical content arrays (`npmContentRules`,
   `dotnetContentRules`) into one `ContentScans` table whose rows
   group a manifest's file selectors with its `ContentPatternRule`
   probes, so a new platform is a data row rather than a new type.
   The ~60-flag contract in the design doc and these tables are the
   same set; no separate fixture extraction is needed. The per-flag tests use the
   existing
   [`workspace-context-detector.test.ts`](../src/AutoContext.VsCode/tests/unit-tests/workspace-context-detector.test.ts)
   fixtures as their porting source.
2. **Cache path migration — sweep deletes orphans.** The
   per-instance cache root has progressed through two preview
   shapes: bare `<workspaceHash>` (earliest) and flat
   `<workspaceHash>#<instanceId>` (intermediate). The shipped
   shape is nested `<workspaceHash>\<instanceId>\` (POSIX: `/`).
   The Phase 2 housekeeping sweep treats any subtree under the
   engine's cache root that does not match the canonical nested
   shape — or matches the shape but has no live `engine-registry.json`
   entry — as stale, and deletes it once it falls outside the
   retention floor (this is the `Foreign` arm of
   `SubtreeRegistryStatus` for the two legacy shapes, and the
   `StaleRegistration` / `Unregistered` arms for canonical-shape
   orphans). The
   cache root is engine-owned (P5), nothing else writes there, and
   cache contents are reproducible, so a stale orphan is just disk
   pressure. The retention floor protects against deleting a
   directory that another engine is mid-write into.
3. **`Workspace.Detect` arbitrary-path RPC — no action needed.**
   Audit of every `workspaceContextDetector` consumer
   (`auto-configurer.ts`, `extension-composition.ts`,
   `extension-activation.ts`, `extension-registrations.ts`,
   `instructions-viewer-code-lens-provider.ts`) confirms every call
   is the parameterless `detector.detect()` that scans the active
   workspace. No caller passes an arbitrary path. The design's
   "engine's `--workspace` only" rule is already what today's
   extension does; Phase 14 simply replaces `detector.detect()`
   with `client.Workspace.Detect()`.

## Companion documents

- [`future/autocontext-engine.md`](./autocontext-engine.md)
  — design authority.
- [`future/autocontext-cli.md`](./future/autocontext-cli.md) — CLI
  subcommands plan, separate from this rollout.
- `architecture-centralized-mcp.md` (repo memory under
  `/memories/repo/`) — current-topology context; provides the project
  layout and naming conventions every phase keeps consistent with.
