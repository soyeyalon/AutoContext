namespace AutoContext.Workers.Core;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Framework.Pipes;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// The worker's task runner. Listens on a named pipe for task requests
/// sent by the MCP server, looks the task up by name in the worker's
/// registered <see cref="IMcpTask"/> set, executes it, and sends the
/// result back on the same connection.
/// </summary>
/// <remarks>
/// Each pipe connection carries exactly one task call: read one request
/// envelope, run one task, write one response envelope, close. The MCP
/// server is responsible for fanning a multi-task tool invocation out
/// across multiple concurrent connections and aggregating the results;
/// the worker only sees individual task calls.
/// <para>
/// Wire protocol: 4-byte little-endian length prefix + UTF-8 JSON
/// payload (see <see cref="LengthPrefixedFrameCodec"/>).
/// </para>
/// <para>
/// Request:  <c>{ "mcpTask", "data", "editorconfig" }</c><br/>
/// Response: <c>{ "mcpTask", "status", "output", "error" }</c>
/// </para>
/// <para>
/// Any <c>editorconfig</c> object on the request envelope is flattened into
/// <c>data</c> as properties prefixed with <c>editorconfig.</c>, and any
/// <c>disabledRules</c> array is copied into <c>data</c> unchanged, before the
/// task is invoked, so tasks see a single payload.
/// </para>
/// </remarks>
public sealed partial class WorkerTaskDispatcherService : BackgroundService
{
    /// <summary>
    /// JSON serialization options used for every wire envelope read/written
    /// by this service. camelCase property naming, no indentation. Frozen at
    /// initialization so misuse (mutation after first serialization) fails fast
    /// instead of silently freezing whatever state happened to be set.
    /// </summary>
    public static JsonSerializerOptions WorkerJsonOptions { get; } = CreateWorkerJsonOptions();

    private readonly ILogger<WorkerTaskDispatcherService> _logger;
    private readonly WorkerHostOptions _options;
    private readonly Dictionary<string, IMcpTask> _tasks;

    /// <summary>
    /// Creates a new <see cref="WorkerTaskDispatcherService"/>.
    /// </summary>
    public WorkerTaskDispatcherService(
        IOptions<WorkerHostOptions> options,
        IEnumerable<IMcpTask> tasks,
        ILogger<WorkerTaskDispatcherService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
        _tasks = [];

        foreach (var task in tasks)
        {
            if (!_tasks.TryAdd(task.TaskName, task))
            {
                throw new InvalidOperationException(
                    $"Duplicate IMcpTask registration for task name '{task.TaskName}'.");
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = _options.Pipe;

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new InvalidOperationException(
                $"Missing required configuration: {nameof(WorkerHostOptions)}.{nameof(WorkerHostOptions.Pipe)}");
        }

        var readyMarker = _options.ReadyMarker;

        if (string.IsNullOrWhiteSpace(readyMarker))
        {
            throw new InvalidOperationException(
                $"Missing required configuration: {nameof(WorkerHostOptions)}.{nameof(WorkerHostOptions.ReadyMarker)}");
        }

        LogStarting(_logger, pipeName);

        var listener = new PipeListener(pipeName, NullLogger<PipeListener>.Instance);
        BoundPipeListener bound;
        try
        {
            bound = listener.Bind();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await using (bound.ConfigureAwait(false))
        {
            LogReady(_logger, pipeName);

            // Host-handshake contract: the parent (extension) process scrapes
            // worker stderr for this exact marker to know the named pipe is
            // listening. ILogger output is routed elsewhere and would miss the
            // contract — this call MUST stay on Console.Error.
            await Console.Error.WriteLineAsync(readyMarker).ConfigureAwait(false);

            await bound.RunAsync(
                HandleConnectionAsync,
                stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var channel = new LengthPrefixedFrameCodec(stream);
            var requestBytes = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (requestBytes is null)
            {
                return;
            }

            var responseBytes = await DispatchAsync(requestBytes, cancellationToken).ConfigureAwait(false);

            await channel.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — exit silently.
        }
        catch (IOException ex)
        {
            // Client disconnected mid-call — log at debug for diagnostics.
            LogConnectionDropped(_logger, ex);
        }
        catch (ObjectDisposedException ex)
        {
            // Pipe disposed during shutdown — log at debug for diagnostics.
            LogConnectionDropped(_logger, ex);
        }
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "Worker boundary: any task failure must be returned as an error envelope, never crash the dispatcher.")]
    private async Task<byte[]> DispatchAsync(byte[] requestBytes, CancellationToken cancellationToken)
    {
        // Parse the envelope first under its own narrow handler. A
        // malformed envelope has no correlation id available, so it
        // can never be scope-correlated — but neither does it execute
        // any user task, so there's nothing to mis-attribute.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(requestBytes);
        }
        catch (JsonException ex)
        {
            return BuildErrorResponse(taskName: "", $"Malformed request JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("mcpTask", out var taskNameElement)
                || taskNameElement.ValueKind != JsonValueKind.String)
            {
                return BuildErrorResponse(taskName: "", "Request is missing required field 'mcpTask'.");
            }

            var taskName = taskNameElement.GetString()!;
            var correlationId = TryReadCorrelationId(root);

            // Push the per-invocation correlation id BEFORE the inner
            // try/catch so every ILogger call made inside ExecuteAsync
            // *and* the LogTaskFailed handler in the catch are both
            // stamped with the same id on the way out the LogServer pipe.
            using var scope = correlationId is null
                ? null
                : CorrelationScope.Push(correlationId);

            try
            {
                if (!_tasks.TryGetValue(taskName, out var task))
                {
                    return BuildErrorResponse(taskName, $"Unknown task '{taskName}'.");
                }

                var data = BuildTaskData(root);
                var output = await task.ExecuteAsync(data, cancellationToken).ConfigureAwait(false);

                return BuildSuccessResponse(taskName, output);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                return BuildErrorResponse(taskName, $"Malformed request JSON: {ex.Message}");
            }
            catch (Exception ex) when (!IsCritical(ex))
            {
                LogTaskFailed(_logger, taskName, ex);
                return BuildErrorResponse(taskName, $"Task threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Critical exceptions that indicate the process is in an
    /// unrecoverable state. These must escape the task-dispatch
    /// catch-all so the host can fail fast instead of silently
    /// converting them into a per-task error envelope.
    /// </summary>
    private static bool IsCritical(Exception ex) =>
        ex is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or ThreadAbortException;

    private static JsonElement BuildTaskData(JsonElement root)
    {
        var hasData = root.TryGetProperty("data", out var dataElement)
            && dataElement.ValueKind == JsonValueKind.Object;

        var hasEditorConfig = root.TryGetProperty("editorconfig", out var ec)
            && ec.ValueKind == JsonValueKind.Object;

        var hasDisabledRules = root.TryGetProperty("disabledRules", out var disabledRules)
            && disabledRules.ValueKind == JsonValueKind.Array;

        if (!hasEditorConfig && !hasDisabledRules)
        {
            return hasData ? dataElement.Clone() : default;
        }

        // Merge editorconfig.<key> properties and the disabled rules into data so tasks see a single payload.
        var merged = hasData
            ? JsonNode.Parse(dataElement.GetRawText()) as JsonObject ?? []
            : [];

        if (hasEditorConfig)
        {
            foreach (var prop in ec.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    merged["editorconfig." + prop.Name] = prop.Value.GetString();
                }
            }
        }

        if (hasDisabledRules)
        {
            merged["disabledRules"] = JsonNode.Parse(disabledRules.GetRawText());
        }

        return JsonSerializer.SerializeToElement(merged);
    }

    /// <summary>
    /// Reads the optional <c>correlationId</c> field from a task request
    /// envelope. Returns <c>null</c> when the field is absent, empty, or
    /// not a string — the dispatcher then proceeds without an active
    /// <see cref="CorrelationScope"/> and log records carry no id.
    /// </summary>
    private static string? TryReadCorrelationId(JsonElement root)
    {
        if (!root.TryGetProperty("correlationId", out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static byte[] BuildSuccessResponse(string taskName, JsonElement output)
    {
        var response = new JsonObject
        {
            ["mcpTask"] = taskName,
            ["status"] = "ok",
            ["output"] = JsonNode.Parse(output.GetRawText()),
            ["error"] = string.Empty,
        };

        return JsonSerializer.SerializeToUtf8Bytes(response, WorkerJsonOptions);
    }

    internal static byte[] BuildErrorResponse(string taskName, string error)
    {
        var response = new JsonObject
        {
            ["mcpTask"] = taskName,
            ["status"] = "error",
            ["output"] = null,
            ["error"] = error,
        };

        return JsonSerializer.SerializeToUtf8Bytes(response, WorkerJsonOptions);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker listening on pipe: {PipeName}")]
    private static partial void LogStarting(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker ready marker emitted on pipe: {PipeName}")]
    private static partial void LogReady(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Task '{TaskName}' threw an unhandled exception.")]
    private static partial void LogTaskFailed(ILogger logger, string taskName, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Pipe connection ended without a complete request/response exchange.")]
    private static partial void LogConnectionDropped(ILogger logger, Exception exception);

    private static JsonSerializerOptions CreateWorkerJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }
}
