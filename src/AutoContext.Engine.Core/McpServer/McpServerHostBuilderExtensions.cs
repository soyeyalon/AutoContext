namespace AutoContext.Engine.Core.McpServer;

using System.Collections.Generic;

using AutoContext.Engine.Core.Features.Instructions;
using AutoContext.Engine.Core.Features.McpTools;
using AutoContext.Engine.Core.Features.McpTools.EditorConfig;
using AutoContext.Engine.Core.Infrastructure;
using AutoContext.Engine.Core.Infrastructure.Diagnostics;
using AutoContext.Engine.Core.Infrastructure.Events;
using AutoContext.Engine.Core.Logging;
using AutoContext.Engine.Core.McpServer.Tools;
using AutoContext.Engine.Core.McpServer.Tools.Intrinsics;
using AutoContext.Engine.Core.McpServer.Tools.Registry;
using AutoContext.Engine.Core.Rpc.Handlers;
using AutoContext.Engine.Core.Workers;
using AutoContext.Engine.Core.Workspace.Config;
using AutoContext.Engine.Core.Workspace.Context;
using AutoContext.Engine.Protocol.Messages.Instructions;
using AutoContext.Framework.Pipes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Composition root for the <c>--mcp-server with-stdio</c> role. Registers
/// the reduced-but-sufficient service graph: the in-process instruction
/// capabilities, the worker-dispatch substrate for the <c>analyze_*</c> /
/// <c>read_*</c> tools, a per-request <c>.autocontext.json</c> reloader, and
/// the two capability handlers the tool leaves marshal into. It deliberately
/// omits every daemon-only mechanism — the four
/// pipes and their endpoint host, subscription broadcasters (beyond the
/// bare seed the instruction handler's constructor needs), the
/// registry-file writer, the <c>engine.log</c> sink, the watchdogs, and
/// the config / workspace file watchers — so the process binds no pipes,
/// writes no registry entry, and attaches no <see cref="FileSystemWatcher"/>.
/// </summary>
internal static class McpServerHostBuilderExtensions
{
    /// <summary>
    /// Registers the stdio MCP-server role on <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">Host application builder to extend.</param>
    /// <param name="configure">Callback that mutates the
    /// <see cref="EngineOptions"/> instance before it is validated.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="configure"/> is
    /// <see langword="null"/>.</exception>
    public static IHostApplicationBuilder AddMcpServer(
        IHostApplicationBuilder builder,
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
        // constant. An absent level leaves the role's own configuration in
        // force, so the quiet stderr default holds unless an operator asks
        // for detail; stdout stays protocol-only either way, since the
        // stderr redirection is independent of the level.
        var loggingProbe = new EngineOptions();
        configure(loggingProbe);
        if (loggingProbe.LogLevel is { } minimumLevel)
        {
            builder.Logging.SetMinimumLevel(minimumLevel);
        }

        builder.Services.TryAddSingleton(TimeProvider.System);

        // In-process log ingest channel. WorkerProcessService and the
        // worker dispatch path write to it; the role runs no drain (no
        // engine.log sink), so records are best-effort and dropped when
        // the bounded channel is full.
        builder.Services.TryAddSingleton<LogChannel>();

        // Connect primitive for the worker-dispatch pipes. These private
        // pipes — namespaced by the ephemeral instance id minted for this
        // process — are the only pipes this role ever binds.
        builder.Services.TryAddSingleton<PipeTransport>();

        // Workspace config store, re-read per MCP request. Constructed but
        // never Watch()ed, so no FileSystemWatcher is attached; the startup
        // loader performs the initial reload and the adapter reloads it on
        // every tools/list / tools/call.
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
        builder.Services.TryAddSingleton<IConfigReloader>(
            sp => sp.GetRequiredService<ConfigFileManager>());

        // Workspace detection rule tables + detector. The startup loader runs
        // a single DetectAsync (no Watch()), so the detector's result is
        // fixed for the process lifetime with no watcher armed.
        builder.Services.TryAddSingleton(WorkspaceDetectionRules.FileRules);
        builder.Services.TryAddSingleton(WorkspaceDetectionRules.ContentScans);
        builder.Services.TryAddSingleton(WorkspaceDetectionRules.FlagActivationEdges);
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

        // Bundled instructions corpus (read-only side-car, no watcher). Loaded
        // on start so the snapshot is populated before the first request.
        builder.Services.TryAddSingleton(sp => new InstructionsManifestService(
            ResolveResources(sp),
            sp.GetRequiredService<ILogger<InstructionsManifestService>>()));
        builder.Services.TryAddSingleton<IInstructionsManifestAccessor>(
            sp => sp.GetRequiredService<InstructionsManifestService>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, InstructionsManifestService>(
                sp => sp.GetRequiredService<InstructionsManifestService>()));

        // Instructions override inventory (one-shot startup scan, no watcher).
        // Reads the configured override roots, so it must start after the
        // startup loader has loaded the config.
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

        // Instructions body projection + raw reads + full-text search + the
        // shared listing projection. Lazily resolved singletons behind the
        // Instructions.* handler.
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
        builder.Services.TryAddSingleton(sp => new InstructionsListProjector(
            sp.GetRequiredService<IInstructionsManifestAccessor>(),
            sp.GetRequiredService<IInstructionsOverridesAccessor>(),
            sp.GetRequiredService<IConfigSnapshotAccessor>(),
            sp.GetRequiredService<IWorkspaceContextAccessor>()));

        // Bundled MCP-tools registry (read-only side-car, no watcher). Loaded
        // on start so the tool surface is populated before the first request.
        builder.Services.TryAddSingleton(sp => new McpToolsRegistryService(
            ResolveResources(sp),
            sp.GetRequiredService<ILogger<McpToolsRegistryService>>()));
        builder.Services.TryAddSingleton<IMcpToolsRegistryAccessor>(
            sp => sp.GetRequiredService<McpToolsRegistryService>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, McpToolsRegistryService>(
                sp => sp.GetRequiredService<McpToolsRegistryService>()));

        // Worker-dispatch substrate. WorkerProcessService spawns each worker
        // lazily on first invoke over the private, ephemeral-id-scoped pipes
        // and keeps it warm for the process lifetime; the container disposes
        // it on host stop, killing any workers it spawned.
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

        // The InstructionsRpcHandler constructor takes the snapshot
        // broadcaster the daemon uses for Instructions.Subscribe. The stdio
        // role never drives Subscribe, but the seed instance keeps the shared
        // handler constructible without a second, subscription-free handler.
        builder.Services.TryAddSingleton(sp => new SnapshotBroadcaster<IReadOnlyList<JsonInstructionsListRow>>(
            sp.GetRequiredService<ILogger<SnapshotBroadcaster<IReadOnlyList<JsonInstructionsListRow>>>>(),
            "Instructions.Subscribe"));

        // The two capability handlers the tool leaves marshal into — the same
        // types the daemon's pipe RPC dispatches into, so the stdio and pipe
        // surfaces stay byte-identical.
        builder.Services.TryAddSingleton<McpToolsRpcHandler>();
        builder.Services.TryAddSingleton<InstructionsRpcHandler>();

        // The tool families. Each source produces IMcpTool leaves; the adapter
        // concatenates every registered source into its flat routing map, so a
        // new family is a new source here — never an adapter edit.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMcpToolSource, InstructionsToolSource>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMcpToolSource, RegistryToolSource>());

        // Startup loader: loads config + one-shot workspace detection.
        // Registered before InstructionsOverridesService so the config and
        // workspace are populated before that one-shot override scan runs.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, McpStdioStartupLoader>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, InstructionsOverridesService>(
                sp => sp.GetRequiredService<InstructionsOverridesService>()));

        StdioMcpServerEntryPoint.AddStdioMcpServer(builder.Services);

        return builder;
    }

    private static EngineResourcesDirectory ResolveResources(IServiceProvider services)
        => EngineResourcesDirectory.ForResources(
            services.GetRequiredService<IOptions<EngineOptions>>().Value);

    private static EngineResourcesDirectory ResolveInstructions(IServiceProvider services)
        => EngineResourcesDirectory.ForInstructions(
            services.GetRequiredService<IOptions<EngineOptions>>().Value);
}
