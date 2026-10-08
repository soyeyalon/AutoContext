namespace AutoContext.Engine.Core.Features.McpTools;

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Engine.Core.Features.McpTools.EditorConfig;
using AutoContext.Engine.Core.Features.McpTools.Snapshot;
using AutoContext.Engine.Core.Infrastructure.Diagnostics;
using AutoContext.Engine.Core.Workers;
using AutoContext.Engine.Core.Workspace.Config;
using AutoContext.Engine.Core.Workspace.Config.Snapshot;
using AutoContext.Engine.Protocol;
using AutoContext.Engine.Protocol.Messages.McpTools;
using AutoContext.Framework.Pipes;

using Microsoft.Extensions.Logging;

/// <summary>
/// Production <see cref="IMcpToolsInvoker"/> that dispatches one tool call
/// to the owning worker over the shared request/response pipe contract.
/// </summary>
/// <remarks>
/// A tool name is not a worker task name. Each tool runs the worker tasks
/// its registry entry lists (<see cref="McpToolsRegistryEntry.ResolvedTasks"/>),
/// one pipe exchange per task, in order, with the same arguments and
/// EditorConfig values; <see cref="ComposeResult"/> then merges the replies
/// into the tool's single result.
/// </remarks>
internal sealed partial class McpToolsInvoker : IMcpToolsInvoker
{
    private const string CorrelationIdPropertyName = "correlationId";
    private const string DataPropertyName = "data";
    private const string DisabledRulesPropertyName = "disabledRules";
    private const string EditorconfigPropertyName = "editorconfig";
    private const string ErrorPropertyName = "error";
    private const string FilePathPropertyName = "filePath";
    private const string FindingsPropertyName = "findings";
    private const string InstructionsFileSuffix = ".instructions.md";
    private const string OutputPropertyName = "output";
    private const string PassedPropertyName = "passed";
    private const string ReportPropertyName = "report";
    private const string StatusError = "error";
    private const string StatusOk = "ok";
    private const string StatusPropertyName = "status";
    private const string TaskPropertyName = "mcpTask";
    private const string TextBlockType = "text";

    private static readonly JsonSerializerOptions WorkerJsonOptions = CreateWorkerJsonOptions();

    private readonly IConfigSnapshotAccessor _configAccessor;
    private readonly IEditorConfigResolver _editorConfigResolver;
    private readonly string _instanceId;
    private readonly ILogger<McpToolsInvoker> _logger;
    private readonly PipeTransport _transport;
    private readonly TimeSpan _waitDeadline;
    private readonly WorkerProcessService _workerProcessService;

    public McpToolsInvoker(
        WorkerProcessService workerProcessService,
        PipeTransport transport,
        string instanceId,
        IEditorConfigResolver editorConfigResolver,
        IConfigSnapshotAccessor configAccessor,
        ILogger<McpToolsInvoker> logger)
        : this(workerProcessService, transport, instanceId, editorConfigResolver, configAccessor, TimeSpan.FromSeconds(30), logger)
    {
    }

    public McpToolsInvoker(
        WorkerProcessService workerProcessService,
        PipeTransport transport,
        string instanceId,
        IEditorConfigResolver editorConfigResolver,
        IConfigSnapshotAccessor configAccessor,
        TimeSpan waitDeadline,
        ILogger<McpToolsInvoker> logger)
    {
        if (waitDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(waitDeadline),
                waitDeadline,
                "Invoke wait deadline must be positive.");
        }

        ArgumentNullException.ThrowIfNull(workerProcessService);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(editorConfigResolver);
        ArgumentNullException.ThrowIfNull(configAccessor);
        ArgumentNullException.ThrowIfNull(logger);

        _workerProcessService = workerProcessService;
        _transport = transport;
        _instanceId = instanceId;
        _editorConfigResolver = editorConfigResolver;
        _configAccessor = configAccessor;
        _waitDeadline = waitDeadline;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<JsonMcpToolsInvokeResult> InvokeAsync(
        McpToolsRegistryEntry tool,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var endpoint = ServiceAddressFormatter.Format($"worker-{tool.WorkerId}", _instanceId);
        var correlationId = CreateCorrelationId();

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(_waitDeadline);

        try
        {
            var editorconfig = await _editorConfigResolver
                .ResolveAsync(TryGetFilePath(arguments), tool.Editorconfig, deadlineCts.Token)
                .ConfigureAwait(false);

            await _workerProcessService
                .EnsureRunningAsync(tool.WorkerId, deadlineCts.Token)
                .ConfigureAwait(false);

            var taskNames = tool.ResolvedTasks;
            var disabledRules = CollectDisabledRules(_configAccessor.Current);
            var responses = new List<McpToolsWorkerTaskResponse>(taskNames.Count);

            foreach (var taskName in taskNames)
            {
                var requestBytes = BuildRequestBytes(taskName, arguments, editorconfig, disabledRules, correlationId);

                var exchange = new PipeTransientExchangeClient(_transport, endpoint);
                await using (exchange.ConfigureAwait(false))
                {
                    var responseBytes = await exchange
                        .ExchangeAsync(requestBytes, deadlineCts.Token)
                        .ConfigureAwait(false);

                    var response = ReadResponse(taskName, responseBytes);
                    LogTaskCompleted(_logger, tool.Name, taskName, response.Status ?? "(missing)", correlationId);
                    responses.Add(response);
                }
            }

            return ComposeResult(tool.Name, responses);
        }
        catch (OperationCanceledException) when (
            deadlineCts.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            var message =
                $"Worker invocation exceeded the {_waitDeadline.TotalSeconds:0.##}s wait deadline.";
            LogInvokeFailed(_logger, tool.Name, endpoint, message, null);
            return ToolError(tool.Name, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or TimeoutException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException
                or ObjectDisposedException
                or ProcessLaunchException<WorkerProcessInfo>)
        {
            var message = $"Worker invocation failed: {ex.Message}";
            LogInvokeFailed(_logger, tool.Name, endpoint, message, ex);
            return ToolError(tool.Name, message);
        }
    }

    internal static byte[] BuildRequestBytes(
        string taskName,
        JsonElement arguments,
        IReadOnlyDictionary<string, string> editorconfig,
        IReadOnlyList<string> disabledRules,
        string correlationId)
    {
        var editorconfigObject = new JsonObject();

        foreach (var (key, value) in editorconfig)
        {
            editorconfigObject[key] = value;
        }

        var request = new JsonObject
        {
            [TaskPropertyName] = taskName,
            [DataPropertyName] = JsonNode.Parse(arguments.GetRawText()),
            [EditorconfigPropertyName] = editorconfigObject,
            [CorrelationIdPropertyName] = correlationId,
        };

        if (disabledRules.Count > 0)
        {
            request[DisabledRulesPropertyName] = new JsonArray([.. disabledRules.Select(static rule => JsonValue.Create(rule))]);
        }

        return JsonSerializer.SerializeToUtf8Bytes(request, WorkerJsonOptions);
    }

    /// <summary>
    /// The rules the workspace has switched off, in the form a finding cites
    /// them: <c>&lt;instructions-file-key&gt;#INST####</c> for a single rule,
    /// or the bare <c>&lt;instructions-file-key&gt;</c> when the whole file is
    /// disabled. Workers drop findings that cite any of them, so a check
    /// never reports a rule the user turned off.
    /// </summary>
    /// <param name="config">The workspace configuration.</param>
    /// <returns>The disabled rules, in configuration order; empty when none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is
    /// <see langword="null"/>.</exception>
    internal static IReadOnlyList<string> CollectDisabledRules(ConfigSnapshot config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rules = new List<string>();

        foreach (var file in config.Instructions)
        {
            if (file.Name is not { } name || !name.EndsWith(InstructionsFileSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var key = name[..^InstructionsFileSuffix.Length];

            if (file.Disabled is true)
            {
                rules.Add(key);
                continue;
            }

            foreach (var rule in file.Rules)
            {
                if (rule.Disabled is true && rule.Id is { } id)
                {
                    rules.Add(key + "#" + id);
                }
            }
        }

        return rules;
    }

    /// <summary>
    /// Merges every worker task's reply into the tool's single result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One reply passes through unchanged: an <c>ok</c> status becomes the
    /// <c>ok</c> arm carrying the task's output, anything else the
    /// tool-error arm.
    /// </para>
    /// <para>
    /// Several replies merge. If any task failed, the tool fails and the
    /// error names every failed task, because a report missing one of its
    /// checks must not read as clean. Otherwise, when every output is an
    /// analyzer report (<c>{ passed, report }</c>), the result is one report
    /// that passes only if every task passed, with the task reports joined
    /// in task order; any other outputs are returned as one content block
    /// per task, in task order.
    /// </para>
    /// </remarks>
    /// <param name="toolName">The invoked tool's name.</param>
    /// <param name="responses">One reply per task, in task order; at least
    /// one.</param>
    /// <returns>The tool's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="toolName"/> or
    /// <paramref name="responses"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="responses"/> is
    /// empty.</exception>
    internal static JsonMcpToolsInvokeResult ComposeResult(
        string toolName,
        IReadOnlyList<McpToolsWorkerTaskResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(responses);

        if (responses.Count == 0)
        {
            throw new ArgumentException("A tool invocation must carry at least one task response.", nameof(responses));
        }

        if (responses.Count == 1)
        {
            return ComposeSingle(toolName, responses[0]);
        }

        var failures = responses
            .Where(static response => !string.Equals(response.Status, StatusOk, StringComparison.Ordinal))
            .Select(static response => $"Task '{response.TaskName}' failed: {DescribeFailure(response)}")
            .ToList();

        if (failures.Count > 0)
        {
            return ToolError(toolName, string.Join(Environment.NewLine, failures));
        }

        var isError = responses.Any(static response => GetIsError(response.Output) == true) ? true : (bool?)null;

        if (TryMergeReports(responses, out var merged))
        {
            return new JsonMcpToolsInvokeOkResult
            {
                Name = toolName,
                Content = GetContent(merged, fallbackText: null),
                IsError = isError,
            };
        }

        return new JsonMcpToolsInvokeOkResult
        {
            Name = toolName,
            Content = [.. responses.SelectMany(static response => GetContent(response.Output, fallbackText: null))],
            IsError = isError,
        };
    }

    /// <summary>
    /// Extracts the optional <c>filePath</c> argument used to resolve
    /// EditorConfig values; returns <see langword="null"/> when absent,
    /// blank, or not a string.
    /// </summary>
    internal static string? TryGetFilePath(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(FilePathPropertyName, out var filePath)
            && filePath.ValueKind == JsonValueKind.String)
        {
            var value = filePath.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static JsonElement CreateTextBlock(string text)
        => JsonSerializer.SerializeToElement(new { type = TextBlockType, text });

    private static string CreateCorrelationId()
        => Guid.NewGuid().ToString("N")[..8];

    private static JsonSerializerOptions CreateWorkerJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };

        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    private static List<JsonElement> GetContent(JsonElement? output, string? fallbackText)
    {
        if (output is { ValueKind: JsonValueKind.Object } outputObject
            && outputObject.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.Array)
        {
            var blocks = new List<JsonElement>(contentElement.GetArrayLength());

            foreach (var block in contentElement.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object)
                {
                    blocks.Add(block.Clone());
                }
            }

            if (blocks.Count > 0)
            {
                return blocks;
            }
        }

        if (output is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } outputValue)
        {
            return [CreateTextBlock(outputValue.GetRawText())];
        }

        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            return [CreateTextBlock(fallbackText)];
        }

        return [];
    }

    private static bool? GetIsError(JsonElement? output)
    {
        if (output is { ValueKind: JsonValueKind.Object } outputObject
            && outputObject.TryGetProperty("isError", out var isError)
            && isError.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return isError.GetBoolean();
        }

        return null;
    }

    private static JsonMcpToolsInvokeResult ComposeSingle(string toolName, McpToolsWorkerTaskResponse response)
    {
        if (string.Equals(response.Status, StatusOk, StringComparison.Ordinal))
        {
            return new JsonMcpToolsInvokeOkResult
            {
                Name = toolName,
                Content = GetContent(response.Output, fallbackText: null),
                IsError = GetIsError(response.Output),
            };
        }

        if (string.Equals(response.Status, StatusError, StringComparison.Ordinal))
        {
            return ToolError(toolName, response.Error, response.Output);
        }

        return ToolError(toolName, DescribeUnknownStatus(response.Status), response.Output);
    }

    private static string DescribeFailure(McpToolsWorkerTaskResponse response)
    {
        if (!string.Equals(response.Status, StatusError, StringComparison.Ordinal))
        {
            return DescribeUnknownStatus(response.Status);
        }

        return string.IsNullOrWhiteSpace(response.Error) ? "the worker reported failure." : response.Error;
    }

    private static string DescribeUnknownStatus(string? status)
        => $"Worker returned unknown status '{status ?? "(missing)"}'.";

    private static McpToolsWorkerTaskResponse ReadResponse(string taskName, byte[] responseBytes)
    {
        using var document = JsonDocument.Parse(responseBytes);
        var root = document.RootElement;

        var status = root.TryGetProperty(StatusPropertyName, out var statusElement)
            && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;

        var output = root.TryGetProperty(OutputPropertyName, out var outputElement)
            ? outputElement.Clone()
            : (JsonElement?)null;

        var error = root.TryGetProperty(ErrorPropertyName, out var errorElement)
            && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;

        return new McpToolsWorkerTaskResponse(taskName, status, output, error);
    }

    /// <summary>
    /// Merges analyzer reports — outputs shaped <c>{ passed, report }</c>,
    /// optionally with a structured <c>findings</c> array — into one report
    /// of the same shape, findings concatenated in task order. Returns
    /// <see langword="false"/> when any output has another shape, so the
    /// caller keeps them apart.
    /// </summary>
    private static bool TryMergeReports(
        IReadOnlyList<McpToolsWorkerTaskResponse> responses,
        out JsonElement merged)
    {
        var passed = true;
        var reports = new List<string>(responses.Count);
        var findings = new JsonArray();

        foreach (var response in responses)
        {
            if (response.Output is not { ValueKind: JsonValueKind.Object } output
                || !output.TryGetProperty(PassedPropertyName, out var passedElement)
                || passedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !output.TryGetProperty(ReportPropertyName, out var reportElement)
                || reportElement.ValueKind != JsonValueKind.String)
            {
                merged = default;
                return false;
            }

            passed &= passedElement.GetBoolean();
            reports.Add(reportElement.GetString()!);

            if (output.TryGetProperty(FindingsPropertyName, out var findingsElement)
                && findingsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in findingsElement.EnumerateArray())
                {
                    findings.Add(JsonNode.Parse(finding.GetRawText()));
                }
            }
        }

        merged = JsonSerializer.SerializeToElement(
            new JsonObject
            {
                [PassedPropertyName] = passed,
                [ReportPropertyName] = string.Join("\n\n", reports),
                [FindingsPropertyName] = findings,
            },
            WorkerJsonOptions);

        return true;
    }

    private static JsonMcpToolsInvokeToolErrorResult ToolError(
        string toolName,
        string? message,
        JsonElement? output = null)
    {
        var error = string.IsNullOrWhiteSpace(message)
            ? $"Tool '{toolName}' reported failure."
            : message;

        return new JsonMcpToolsInvokeToolErrorResult
        {
            Name = toolName,
            Content = GetContent(output, error),
        };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "MCP tool invoke failed for '{ToolName}' on worker endpoint '{Endpoint}': {Reason}")]
    private static partial void LogInvokeFailed(
        ILogger logger,
        string toolName,
        string endpoint,
        string reason,
        Exception? exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "MCP tool '{ToolName}' ran worker task '{TaskName}': status '{Status}' (correlation '{CorrelationId}')")]
    private static partial void LogTaskCompleted(
        ILogger logger,
        string toolName,
        string taskName,
        string status,
        string correlationId);
}
