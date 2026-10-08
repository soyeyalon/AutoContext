namespace AutoContext.Worker.Workspace.Tasks.EditorConfig;

using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Workers.Core;

/// <summary>
/// <c>get_editorconfig_rules</c> — resolves a filtered subset of effective
/// <c>.editorconfig</c> properties for a given file path.
/// </summary>
/// <remarks>
/// Request <c>data</c>:  <c>{ "filePath": "&lt;abs-path&gt;", "keys": ["k1", "k2", ...] }</c><br/>
/// <c>filePath</c> is the name the engine's <c>read_editorconfig_rules</c> tool
/// passes; <c>path</c> — sent by the engine's own EditorConfig resolver and by
/// the legacy MCP server — is read when <c>filePath</c> is absent.<br/>
/// Response <c>output</c>: flat <c>{ "k1": "v1", ... }</c> map; missing keys are omitted.
/// </remarks>
internal sealed class GetEditorConfigRulesTask : IMcpTask
{
    public string TaskName => "get_editorconfig_rules";

    public Task<JsonElement> ExecuteAsync(JsonElement data, CancellationToken cancellationToken)
    {
        var path = (data.ValueKind == JsonValueKind.Object
                ? TryGetString(data, "filePath") ?? TryGetString(data, "path")
                : null)
            ?? throw new InvalidOperationException("'data.filePath' (or 'data.path') is required and must be a string.");

        var requestedKeys = ReadKeys(data);

        var resolved = EditorConfigResolver.Resolve(path);
        var filtered = new JsonObject();

        if (requestedKeys is null)
        {
            // No filter: return all resolved keys.
            foreach (var (key, value) in resolved)
            {
                filtered[key] = value;
            }
        }
        else
        {
            foreach (var key in requestedKeys)
            {
                if (resolved.TryGetValue(key, out var value))
                {
                    filtered[key] = value;
                }
            }
        }

        return Task.FromResult(JsonSerializer.SerializeToElement(filtered));
    }

    private static List<string>? ReadKeys(JsonElement data)
    {
        if (!data.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>(keys.GetArrayLength());

        foreach (var k in keys.EnumerateArray())
        {
            if (k.ValueKind == JsonValueKind.String)
            {
                list.Add(k.GetString()!);
            }
        }

        return list;
    }

    private static string? TryGetString(JsonElement data, string property)
        => data.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
