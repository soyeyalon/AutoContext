namespace AutoContext.Engine.Core;

using AutoContext.Engine.Core.Endpoints;
using AutoContext.Engine.Core.Features.Discovery;
using AutoContext.Engine.Core.Features.Instructions;
using AutoContext.Engine.Core.Features.McpTools;
using AutoContext.Engine.Core.Features.McpTools.EditorConfig;
using AutoContext.Engine.Core.Infrastructure;
using AutoContext.Engine.Core.Infrastructure.Diagnostics;
using AutoContext.Engine.Core.Infrastructure.Events;
using AutoContext.Engine.Core.Infrastructure.Storage;
using AutoContext.Engine.Core.Lifecycle;
using AutoContext.Engine.Core.Logging;
using AutoContext.Engine.Core.Machine;
using AutoContext.Engine.Core.Machine.Housekeeping;
using AutoContext.Engine.Core.McpServer;
using AutoContext.Engine.Core.Registry;
using AutoContext.Engine.Core.Rpc.Handlers;
using AutoContext.Engine.Core.Rpc.Policies;
using AutoContext.Engine.Core.Watchdogs;
using AutoContext.Engine.Core.Workers;
using AutoContext.Engine.Core.Workspace.Config;
using AutoContext.Engine.Core.Workspace.Context;
using AutoContext.Engine.Protocol.Messages.Agent;
using AutoContext.Engine.Protocol.Messages.Config;
using AutoContext.Engine.Protocol.Messages.Instructions;
using AutoContext.Engine.Protocol.Messages.Logs;
using AutoContext.Framework.Pipes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Composition root for <c>AutoContext.Engine.Core</c>. Registering
/// the engine with <see cref="AddAutoContextEngine"/> binds
/// <see cref="EngineOptions"/> into the host's options pipeline,
/// installs the shape validator, and prepares the DI graph for the
/// engine's hosted services. The engine binary's <c>Program.Main</c>
/// and every test harness call this method; nothing else does.
/// </summary>
/// <remarks>
/// Per <c>design § Composition contracts</c> the engine library exposes
/// one top-level extension per host role: <see cref="AddAutoContextEngine"/>
/// for the daemon role and <see cref="AddMcpServer"/> for the
/// <c>--mcp-server with-stdio</c> role. New daemon capabilities continue to
/// land behind <see cref="AddAutoContextEngine"/>.
/// </remarks>
public static class EngineHostBuilderExtensions
{
    /// <summary>
    /// Registers the AutoContext engine on
    /// <paramref name="builder"/>'s service collection.
    /// </summary>
    /// <param name="builder">Host application builder to extend.
    /// Must not be <see langword="null"/>.</param>
    /// <param name="configure">Callback that mutates the
    /// <see cref="EngineOptions"/> instance before it is validated.
    /// Invoked once here to resolve the logging level and again when
    /// the options pipeline materialises the instance, so it must be
    /// free of side effects. Must not be <see langword="null"/>.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="configure"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static IHostApplicationBuilder AddAutoContextEngine(
        this IHostApplicationBuilder builder,
        Action<EngineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddOptions<EngineOptions>()
            .Configure(configure)
            .ValidateOnStart();

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<EngineOptions>, EngineOptionsValidator>());

        // Resolve the level eagerly and hand the logging pipeline a
        // constant. Binding LoggerFilterOptions to IOptions<EngineOptions>
        // instead would drag option validation into logger construction,
        // surfacing an invalid workspace path as a logging failure. An
        // absent level leaves the host's own configuration in force.
        var loggingProbe = new EngineOptions();
        configure(loggingProbe);
        if (loggingProbe.LogLevel is { } minimumLevel)
        {
            builder.Logging.SetMinimumLevel(minimumLevel);
        }

        // Clock source for hosted services that stamp wire-visible
        // timestamps (RegistryFileService's own-entry row et al.).
        // Idempotent — a host that already has a TimeProvider keeps
        // its own.
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Registration order encodes the stop order (hosted
        // services stop in reverse). The log pipeline registers
        // first so it stops LAST — every other hosted service,
        // including RegistryFileService and the watchdogs below,
        // can still log through the live drain loop during its
        // own StopAsync. RegistryFileService then stops next-to-
        // last so future hosted writers (housekeeping,
        // crash-writers) registered after it can still await one
        // final WriteAsync through its still-live channel. The
        // file service itself owns the lifecycle of this
        // engine's own row — append on Start, best-effort
        // remove on Stop — so the writer/file-service split
        // that earlier drafts proposed is collapsed into a
        // single service.

        // Engine logging pipeline: LogChannel is the
        // in-process ingest channel; LogFileSinkService drains it
        // into <cacheRoot>/<workspaceHash>/<instanceId>/logs/engine.log.
        // Registered BEFORE RegistryFileService so it stops AFTER
        // it — hosted services stop in reverse registration order,
        // so the registry's own teardown can still log through
        // the live drain loop; the file service then has the
        // final word on its own row. The worker-bound producer
        // (Engine.WriteLog) lands in a later phase.
        builder.Services.TryAddSingleton<LogChannel>();

        // Per-instance identity bundle: workspace hash, instance id,
        // and the resolved cache-root subtree. Every consumer that
        // needs a path under the cache root composes off this
        // singleton (directly, or via EngineCacheLayout below).
        builder.Services.TryAddSingleton<CacheRoot>();

        // Single source of truth for every on-disk path the engine
        // owns under its cache root — per-instance logs / crash
        // tombstone, plus the shared liveness registry file at the
        // cache-root level. Producers and consumers resolve through
        // this singleton so each path is defined once.
        builder.Services.TryAddSingleton<EngineCacheLayout>();

        // Forward-pass NDJSON reader over the active engine.log,
        // consumed by the Logs.GetEngine RPC handler.
        builder.Services.TryAddSingleton<LogFileReader>();

        // Rotation + retention support for the file sink. The
        // thresholds factory pins itself to the resolved
        // EngineOptions.LogRotation size at first resolve; the
        // singletons composed below are read-only after startup.
        // RetentionPolicy is the sole reader of
        // EngineOptions.Retention — both the rotated-log cleaner
        // here and the cross-instance subtree cleaner consult it
        // instead of reading the option directly.
        builder.Services.TryAddSingleton<RetentionPolicy>();
        builder.Services.TryAddSingleton(sp =>
        {
            var rotationSize = sp.GetRequiredService<IOptions<EngineOptions>>().Value.LogRotation;
            return LogRotationThresholds.ForRotationSize(rotationSize);
        });
        builder.Services.TryAddSingleton<RotatedLogCleaner>();

        // Logs-pipe fan-out broadcaster. Sibling consumer of every
        // record drained by LogFileSinkService — file sink and
        // broadcaster receive each record symmetrically. Per-
        // subscriber bounded buffers and slow-subscriber drop
        // shield the file sink from a stalled pipe consumer.
        // Registered as a singleton so EndpointHostService's logs-pipe
        // pump and the file sink share the same instance.
        builder.Services.TryAddSingleton(sp => new Broadcaster<JsonLogRecord>(
            sp.GetRequiredService<ILogger<Broadcaster<JsonLogRecord>>>(),
            "logs-pipe"));

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, LogFileSinkService>());

        // Engine ILogger<T> → LogChannel routing. Registered as an
        // ILoggerProvider on the host's logging pipeline so every
        // engine record materialises as a LogRecord on the channel
        // (alongside the framework default console/debug providers
        // Host.CreateApplicationBuilder installs). The provider
        // does not own the channel — the file-sink service above
        // is the channel's terminator — so this registration's
        // order relative to LogFileSinkService is immaterial for
        // teardown.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, EngineLoggerProvider>());

        builder.Services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
            var clock = sp.GetRequiredService<TimeProvider>();
            var layout = sp.GetRequiredService<EngineCacheLayout>();
            return new RegistryFileService(
                layout.RegistryFilePath,
                sp.GetRequiredService<ILoggerFactory>(),
                ownEntryFactory: () => RegistryEntryBuilder.Build(options, clock));
        });
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RegistryFileService>(
                sp => sp.GetRequiredService<RegistryFileService>()));

        // Stateless concurrent reader used by the RPC dispatcher's
        // Engine.RegistryEntries handler. Bound to the same
        // resolved registry path as the writer so the two see the
        // same file.
        builder.Services.TryAddSingleton(sp =>
        {
            var layout = sp.GetRequiredService<EngineCacheLayout>();
            return new RegistryFileReader(
                layout.RegistryFilePath,
                sp.GetRequiredService<ILogger<RegistryFileReader>>());
        });

        // Liveness-aware view over the registry: composes the
        // stateless RegistryFileReader above with IProcessLookup
        // (registered alongside HostWatchdog further down) and tags
        // each entry Live/Stale via Process.StartTime comparison.
        // CacheRootScanner consumes this to derive the registration
        // half of its SubtreeRegistryStatus output.
        builder.Services.TryAddSingleton<RegistryEntryReader>();

        // Housekeeping cache-root scanner: walks the cache root
        // once and classifies every child directory into one of
        // four SubtreeRegistryStatus arms by composing
        // RegistryEntryReader with a structural shape check.
        // Pure read + classification — no deletion. Consumed by
        // StaleSubtreeCleaner and the HousekeepingService
        // shutdown sweep below.
        builder.Services.TryAddSingleton<CacheRootScanner>();

        // Pattern-matches the scanner's SubtreeRegistryStatus
        // output and deletes each subtree whose retention window
        // has elapsed. Registered subtrees are never touched;
        // stale-registration subtrees honour the entry's own
        // retention; unregistered and foreign subtrees fall back
        // to this engine's --retention via RetentionPolicy.
        builder.Services.TryAddSingleton<StaleSubtreeCleaner>();

        // HousekeepingService is the only hosted service in the
        // engine that runs work in StopAsync only (no startup
        // sweep — every spawn gets a fresh <instanceId>).
        // Registered AFTER RegistryFileService and BEFORE
        // EndpointHostService so the host stops it BEFORE the
        // registry file service tears down — the sweep observes
        // the on-disk registry in its post-pipe-close shape
        // while RegistryFileService's channel is still live —
        // and AFTER EndpointHostService closes the four pipes.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, HousekeepingService>());

        builder.Services.TryAddSingleton<LifecycleEventStream>();
        builder.Services.TryAddSingleton<LifecycleNotifier>();

        // Idle-timeout watchdog: registered as a singleton (so
        // EndpointHostService can inject it directly for keep-alive
        // accounting) and as an IHostedService (so it arms its
        // countdown on host start and disarms on host stop).
        // Registered BEFORE EndpointHostService so it stops AFTER it
        // — EndpointHostService's StopAsync tears down accept loops
        // first, then the watchdog cancels its timer.
        builder.Services.TryAddSingleton<IdleTimeoutWatchdog>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, IdleTimeoutWatchdog>(
                sp => sp.GetRequiredService<IdleTimeoutWatchdog>()));

        // Parent-process watchdog. Standalone hosted service —
        // no per-connection coupling — clamps engine lifetime to
        // the spawner's lifetime when --parent-pid is set, no-op
        // otherwise. Registered after the idle watchdog so it
        // stops in the same window and before EndpointHostService so
        // its StopAsync runs after the dispatcher tears down.
        builder.Services.TryAddSingleton<IProcessLookup, SystemProcessLookup>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, HostWatchdog>());

        // Pre-bind unique-instance guard: EndpointHostService
        // resolves this and invokes EnsureUniqueAsync at the top
        // of StartAsync, before any pipe bind, so a launcher-bug
        // instance-id collision surfaces as a clear diagnostic
        // instead of an opaque pipe-bind error.
        // PipeTransport is the connect primitive the guard's
        // probe rides on; registered as a singleton because the
        // type is stateless and depended on by both the guard
        // and (later) the registry-sweep liveness probes.
        builder.Services.TryAddSingleton<PipeTransport>();
        builder.Services.TryAddSingleton<IUniqueInstanceGuard, PerWorkspaceInstanceGuard>();

        // Workspace config store. The manager owns the in-memory
        // .autocontext.json snapshot for this workspace; it is the
        // singleton source both the Config.Get RPC handler (via the
        // IConfigSnapshotAccessor read seam) and future config writers
        // resolve. ConfigFileService loads the snapshot from disk
        // and arms the file watcher at startup. Registered BEFORE
        // EndpointHostService so it starts first — the snapshot is
        // populated before the first rpc connection can issue
        // Config.Get — and stops after the pipes are torn down. The
        // manager is an IDisposable singleton the container disposes
        // on host stop.
        builder.Services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
            return new ConfigFileManager(
                options.WorkspacePath,
                EngineVersion.Value,
                sp.GetRequiredService<TimeProvider>(),
                ConfigFileManager.DefaultDebounceDelay,
                ConfigFileManager.DefaultBatchWindow,
                sp.GetRequiredService<ILogger<ConfigFileManager>>());
        });
        builder.Services.TryAddSingleton<IConfigSnapshotAccessor>(
            sp => sp.GetRequiredService<ConfigFileManager>());
        builder.Services.TryAddSingleton<IConfigUpdater>(
            sp => sp.GetRequiredService<ConfigFileManager>());
        builder.Services.TryAddSingleton<IConfigChangeNotifier>(
            sp => sp.GetRequiredService<ConfigFileManager>());

        // Bundled instructions corpus. The service loads the two
        // build-time side-cars shipped beside the engine binary
        // (instructions-manifest.json + instructions-catalog.json) into
        // an immutable snapshot at start and holds it for the
        // Instructions.* RPC handlers, which read it through the
        // IInstructionsManifestAccessor seam. Registered BEFORE
        // EndpointHostService so the snapshot is populated before the first
        // rpc connection can issue an Instructions.* request. The corpus
        // is read-only with no watcher, so the service only loads on
        // start and tears nothing down on stop.
        builder.Services.TryAddSingleton(sp => new InstructionsManifestService(
            ResolveResources(sp),
            sp.GetRequiredService<ILogger<InstructionsManifestService>>()));
        builder.Services.TryAddSingleton<IInstructionsManifestAccessor>(
            sp => sp.GetRequiredService<InstructionsManifestService>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, InstructionsManifestService>(
                sp => sp.GetRequiredService<InstructionsManifestService>()));

        // Bundled MCP-tools registry. The service loads the build-time
        // side-cars shipped beside the engine binary
        // (mcp-tools-registry.json + mcp-tools-catalog.json, each with its
        // schema) into an immutable snapshot at start and holds it for the
        // McpTools.* RPC handlers, which read it through the
        // IMcpToolsRegistryAccessor seam. Registered BEFORE EndpointHostService
        // so the snapshot is populated before the first rpc connection can
        // issue a McpTools.* request. The registry is read-only with no
        // watcher, so the service only loads on start and tears nothing
        // down on stop.
        builder.Services.TryAddSingleton(sp => new McpToolsRegistryService(
            ResolveResources(sp),
            sp.GetRequiredService<ILogger<McpToolsRegistryService>>()));
        builder.Services.TryAddSingleton<IMcpToolsRegistryAccessor>(
            sp => sp.GetRequiredService<McpToolsRegistryService>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, McpToolsRegistryService>(
                sp => sp.GetRequiredService<McpToolsRegistryService>()));

        // Worker-dispatch substrate. The engine spawns workers lazily —
        // WorkerProcessService.EnsureRunningAsync(workerId) starts a worker
        // the first time a tool routed to it is invoked and reuses the live
        // process thereafter. The service resolves its launch specifications
        // when the host starts it: the provider reads the build-generated
        // workers.json side-car, expands each row's ${root} placeholder to
        // that worker's staging subdir under Workers/, and threads the engine
        // instance id onto every spawn so worker and engine derive the same
        // listen endpoint. Resolving the provider at StartAsync — not during
        // construction — keeps the manifest read off the DI resolution path,
        // so a missing or malformed side-car fails host start loudly rather
        // than the first dispatch, mirroring the instructions and registry
        // services. Registered as an IHostedService BEFORE EndpointHostService so
        // its hosts are populated before the first rpc connection can dispatch
        // a tool. The launcher (process creation) and connection probe
        // (readiness dial over the shared PipeTransport) are the seams the
        // service drives. WorkerProcessService is an IDisposable singleton the
        // container disposes on host stop, which kills any workers still
        // running.
        builder.Services.TryAddSingleton<IProcessLauncher<WorkerProcessInfo>, WorkerProcessLauncher>();
        builder.Services.TryAddSingleton<IWorkerConnectionProbe, WorkerConnectionProbe>();
        builder.Services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;

            return new WorkerProcessService(
                () => WorkerProcessInfoResolver.Resolve(
                    WorkersManifestLoader.Load(EngineResourcesDirectory.ForResources(options)),
                    Path.Combine(AppContext.BaseDirectory, "Workers"),
                    options.InstanceId.ToString("D"),
                    options.WorkspacePath),
                sp.GetRequiredService<IProcessLauncher<WorkerProcessInfo>>(),
                sp.GetRequiredService<IWorkerConnectionProbe>(),
                sp.GetRequiredService<LogChannel>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<WorkerProcessService>>());
        });
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, WorkerProcessService>(
                sp => sp.GetRequiredService<WorkerProcessService>()));

        // Read-only view over which workers have ever been spawned,
        // backing the Logs.GetWorker / Logs.TailWorker not-found
        // decision (never-spawned worker vs. spawned-but-quiet).
        builder.Services.TryAddSingleton<IWorkerSpawnTracker>(
            sp => sp.GetRequiredService<WorkerProcessService>());

        // MCP-tools dispatch seam. The invoker round-trips one tool call to
        // its owning worker over the shared request/response pipe contract,
        // spawning the worker lazily via WorkerProcessService on first invoke;
        // the editorconfig resolver is the engine's single editorconfig hop
        // (resolution lives in Worker.Workspace, never in-process). Both are
        // built from the already-registered WorkerProcessService +
        // PipeTransport singletons plus the engine instance id. EndpointHostService
        // injects IMcpToolsInvoker directly: WorkerProcessService now resolves
        // its manifest at StartAsync, so constructing the invoker (and the
        // worker service it depends on) at host startup is side-effect-free.
        builder.Services.TryAddSingleton<IEditorConfigResolver>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
            return new WorkerEditorConfigResolver(
                sp.GetRequiredService<WorkerProcessService>(),
                sp.GetRequiredService<PipeTransport>(),
                options.InstanceId.ToString("D"),
                sp.GetRequiredService<ILogger<WorkerEditorConfigResolver>>());
        });
        builder.Services.TryAddSingleton<IMcpToolsInvoker>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
            return new McpToolsInvoker(
                sp.GetRequiredService<WorkerProcessService>(),
                sp.GetRequiredService<PipeTransport>(),
                options.InstanceId.ToString("D"),
                sp.GetRequiredService<IEditorConfigResolver>(),
                sp.GetRequiredService<IConfigSnapshotAccessor>(),
                sp.GetRequiredService<ILogger<McpToolsInvoker>>());
        });

        // Instructions override inventory. The service performs a one-shot
        // startup scan of the workspace's override directories and exposes
        // the result through the IInstructionsOverridesAccessor seam the
        // Instructions.* handlers read. Its hosted lifetime is registered
        // AFTER ConfigFileService (below) so the configured override roots
        // are loaded before the scan runs; the singleton mapping here is
        // lazily resolved so the registration order is irrelevant. The
        // scan compares each override against the bundled file it shadows
        // through the InstructionsOverridesStalenessInspector and warns on
        // stale overrides.
        builder.Services.TryAddSingleton(sp => new InstructionsOverridesStalenessInspector(
            ResolveInstructions(sp),
            sp.GetRequiredService<ILogger<InstructionsOverridesStalenessInspector>>()));
        builder.Services.TryAddSingleton(sp => new InstructionsOverridesService(
            sp.GetRequiredService<IWorkspaceContextAccessor>(),
            sp.GetRequiredService<IConfigSnapshotAccessor>(),
            sp.GetRequiredService<InstructionsOverridesStalenessInspector>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<ILogger<InstructionsOverridesService>>()));
        builder.Services.TryAddSingleton<IInstructionsOverridesAccessor>(
            sp => sp.GetRequiredService<InstructionsOverridesService>());

        // Instructions body projection + raw file reads + full-text search.
        // All read the bundled corpus body files shipped beside the engine
        // binary under Instructions/ and resolve override bodies
        // through the accessor above. Lazily resolved singletons backing the
        // Instructions.Get / GetAll / GetAlwaysAttached / GetRaw /
        // SearchContent handlers.
        builder.Services.TryAddSingleton(sp => new InstructionsBodyProjector(
            ResolveInstructions(sp),
            sp.GetRequiredService<IInstructionsOverridesAccessor>(),
            sp.GetRequiredService<IConfigSnapshotAccessor>()));
        builder.Services.TryAddSingleton(sp => new InstructionsFileReader(
            ResolveInstructions(sp),
            sp.GetRequiredService<IInstructionsOverridesAccessor>()));
        builder.Services.TryAddSingleton(sp => new InstructionsFullTextSearchService(
            sp.GetRequiredService<IInstructionsManifestAccessor>(),
            sp.GetRequiredService<InstructionsBodyProjector>(),
            sp.GetRequiredService<IConfigSnapshotAccessor>(),
            sp.GetRequiredService<ILogger<InstructionsFullTextSearchService>>()));

        // Shared corpus-listing projection. Single source of the
        // per-row listing shape — disabled resolution, override
        // source, section mapping — so the List RPC and the
        // Instructions.Subscribe snapshot frame project each row
        // identically (the row set still differs: List defaults to
        // workspace filtering, Subscribe projects the whole corpus).
        // Lazily resolved so its accessor seams need not be ordered
        // ahead of it.
        builder.Services.TryAddSingleton(sp => new InstructionsListProjector(
            sp.GetRequiredService<IInstructionsManifestAccessor>(),
            sp.GetRequiredService<IInstructionsOverridesAccessor>(),
            sp.GetRequiredService<IConfigSnapshotAccessor>(),
            sp.GetRequiredService<IWorkspaceContextAccessor>()));

        // Workspace context detection rule tables. The three declarative
        // tables — file presence, content scans (npm + .NET, grouped by
        // manifest), and the flag activation cascade — are static data
        // ported from the VS Code extension's detector. They register as
        // IReadOnlyList<T> singletons so the detector composes off them
        // via constructor injection and tests can swap in trimmed lists.
        builder.Services.TryAddSingleton(WorkspaceDetectionRules.FileRules);
        builder.Services.TryAddSingleton(WorkspaceDetectionRules.ContentScans);
        builder.Services.TryAddSingleton(WorkspaceDetectionRules.FlagActivationEdges);

        // Config-subscription fan-out broadcaster. Singleton so the
        // RPC Config.Subscribe handler and the ConfigFileService
        // change-event bridge share one instance: the bridge primes
        // the snapshot seed and publishes every change, the handler
        // enrolls snapshot-seeded subscribers.
        builder.Services.TryAddSingleton(sp => new SnapshotBroadcaster<JsonConfigSnapshot>(
            sp.GetRequiredService<ILogger<SnapshotBroadcaster<JsonConfigSnapshot>>>(),
            "Config.Subscribe"));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ConfigFileService>());

        // Instructions-subscription fan-out broadcaster. Singleton so the
        // RPC Instructions.Subscribe handler and the
        // InstructionsSubscriptionService share one instance: the service
        // primes the snapshot seed at startup and republishes the listing
        // on every config change, the handler enrolls snapshot-seeded
        // subscribers.
        builder.Services.TryAddSingleton(sp => new SnapshotBroadcaster<IReadOnlyList<JsonInstructionsListRow>>(
            sp.GetRequiredService<ILogger<SnapshotBroadcaster<IReadOnlyList<JsonInstructionsListRow>>>>(),
            "Instructions.Subscribe"));

        // Instructions override scan. Hosted lifetime registered AFTER
        // ConfigFileService so the configured InstructionsOverridesRoots are
        // loaded before the one-shot startup scan reads them.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, InstructionsOverridesService>(
                sp => sp.GetRequiredService<InstructionsOverridesService>()));

        // Workspace context detector. The detector owns the in-memory
        // detection result and workspace-info metadata for this
        // workspace; it is the singleton source the Workspace.Detect and
        // Workspace.Info RPC handlers resolve via the
        // IWorkspaceContextAccessor read seam. WorkspaceDetectionService
        // runs the initial scan and arms the filesystem watcher at
        // startup. Registered BEFORE EndpointHostService so it starts first —
        // the result is populated before the first rpc connection can
        // issue Workspace.Detect — and the detector is an IDisposable
        // singleton the container disposes on host stop, tearing down its
        // watcher.
        builder.Services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
            return new WorkspaceContextDetector(
                options,
                sp.GetRequiredService<IReadOnlyList<FilePresenceRule>>(),
                sp.GetRequiredService<IReadOnlyList<ContentScan>>(),
                sp.GetRequiredService<IReadOnlyList<FlagActivationEdge>>(),
                sp.GetRequiredService<TimeProvider>(),
                WorkspaceContextDetector.DefaultDebounceDelay,
                sp.GetRequiredService<ILogger<WorkspaceContextDetector>>());
        });
        builder.Services.TryAddSingleton<IWorkspaceContextAccessor>(
            sp => sp.GetRequiredService<WorkspaceContextDetector>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, WorkspaceDetectionService>());

        // Instructions snapshot priming. Hosted lifetime registered AFTER
        // WorkspaceDetectionService so the manifest, override, config, and
        // workspace accessors it projects are fully populated, and BEFORE
        // EndpointHostService so the snapshot seed is primed before the first
        // events-pipe connection can enroll an Instructions.Subscribe
        // subscriber. The service also bridges config changes into the
        // broadcaster, republishing the re-projected listing on each
        // Config.Toggle* edit.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, InstructionsSubscriptionService>());

        // Per-feature RPC method handlers. DispatchPolicy injects the full
        // IRpcMethodHandler set and builds a method-keyed router; each
        // handler declares the JSON-RPC methods it serves.
        builder.Services.TryAddSingleton<DiscoveryService>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, McpToolsRpcHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, InstructionsRpcHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, ConfigRpcHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, LogsRpcHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, WriteLogRpcHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, RegistryRpcHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, WorkspaceRpcHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, DiscoveryRpcHandler>());

        // Agent-event fan-out: the shared broadcaster AgentRpcHandler
        // publishes each mapped Agent.* notification to and that
        // Agent.Events.Subscribe drains (pure live tail).
        builder.Services.TryAddSingleton(sp => new Broadcaster<JsonAgentEvent>(
            sp.GetRequiredService<ILogger<Broadcaster<JsonAgentEvent>>>(),
            "agent-events"));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRpcMethodHandler, AgentRpcHandler>());

        // The dispatch router is a stateless singleton: its method table is
        // built once from the handler set above and shared across every rpc
        // connection.
        builder.Services.TryAddSingleton(serviceProvider => new DispatchPolicy(
            serviceProvider.GetRequiredService<IHostApplicationLifetime>(),
            serviceProvider.GetServices<IRpcMethodHandler>(),
            serviceProvider.GetRequiredService<ILogger<DispatchPolicy>>()));

        // Per-kind connection handlers. EndpointHostService injects the
        // full IEndpointHandler set and maps each by its Kind; a kind
        // with no handler (health) is accepted and closed.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEndpointHandler, RpcEndpointHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEndpointHandler, EventsEndpointHandler>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEndpointHandler, LogsEndpointHandler>());

        // Shared shutdown-drain deadline: the host arms it during
        // StopAsync and the events/logs handlers observe its token so a
        // peer that stops reading mid-shutdown cannot wedge teardown.
        builder.Services.TryAddSingleton<ShutdownDrainDeadline>();

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, EndpointHostService>());

        return builder;
    }

    /// <summary>
    /// Registers the reduced dependency graph for the
    /// <c>--mcp-server with-stdio</c> role on
    /// <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">Host application builder to extend.</param>
    /// <param name="configure">Callback that mutates the
    /// <see cref="EngineOptions"/> instance before it is validated.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="configure"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static IHostApplicationBuilder AddMcpServer(
        this IHostApplicationBuilder builder,
        Action<EngineOptions> configure)
        => McpServerHostBuilderExtensions.AddMcpServer(builder, configure);

    private static EngineResourcesDirectory ResolveResources(IServiceProvider services)
        => EngineResourcesDirectory.ForResources(
            services.GetRequiredService<IOptions<EngineOptions>>().Value);

    private static EngineResourcesDirectory ResolveInstructions(IServiceProvider services)
        => EngineResourcesDirectory.ForInstructions(
            services.GetRequiredService<IOptions<EngineOptions>>().Value);
}
