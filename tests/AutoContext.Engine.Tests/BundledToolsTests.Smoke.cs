namespace AutoContext.Engine.Tests;

using System.Text.Json;

using AutoContext.Engine.Protocol.Messages.McpTools;
using AutoContext.Engine.Protocol.Serialization;
using AutoContext.Engine.Tests.Support.Diagnostics;
using AutoContext.Engine.Tests.Support.IO;
using AutoContext.Engine.Tests.Support.Mcp;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>
/// Calls every tool the staged bundle registers, through the real engine in
/// its stdio MCP-server role and the real workers, the way a host does. The
/// other end-to-end suites route worker-backed calls to a test-driver echo
/// tool, so nothing else proves that a <em>shipped</em> tool reaches a worker
/// task that accepts its arguments: a registry tool whose name or parameters
/// drift from the worker it dispatches to answers <c>Unknown task</c>, or
/// silently ignores an argument, and only a call like these exposes it.
/// </summary>
/// <remarks>
/// Every registry tool must have a fixture here, so adding a tool without
/// proving it callable fails the suite. Gated with the repository's
/// <c>Category=Smoke</c> trait: the bundle exists only after
/// <c>.\scripts\test.ps1 -Smoke DotNet</c> has staged it.
/// </remarks>
[Trait("Category", "Smoke")]
public sealed class BundledToolsTests
{
    [Fact]
    public async Task Should_answer_every_registered_tool_with_a_report()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EngineBundlePath.RequireStaged();

        using var cache = IsolatedCacheRoot.Create();
        using var workspace = WorkspaceTestDirectoryFactory.Create();
        var fixtures = WriteFixtures(workspace.Path);
        var registered = ReadRegisteredToolNames(Path.Combine(EngineBundlePath.Resources, "mcp-tools-registry.json"));

        await using var client = await StdioMcpServerClient.CreateFromBundleAsync(workspace.Path, cache.Path, ct);

        // Act
        var outcomes = new Dictionary<string, JsonMcpToolsInvokeResult>(StringComparer.Ordinal);

        foreach (var name in registered)
        {
            if (fixtures.TryGetValue(name, out var arguments))
            {
                outcomes[name] = await CallToolAsync(client, name, arguments, ct);
            }
        }

        // Assert
        Assert.Multiple(
            [
                () => Assert.Empty(registered.Except(fixtures.Keys)),
                .. outcomes.Select<KeyValuePair<string, JsonMcpToolsInvokeResult>, Action>(
                    outcome => () => Assert.True(
                        outcome.Value is JsonMcpToolsInvokeOkResult,
                        $"Tool '{outcome.Key}' did not return a report: {Describe(outcome.Value)}")),
            ]);
    }

    [Fact]
    public async Task Should_hand_the_file_path_argument_to_the_worker()
    {
        // Arrange — a report alone does not prove an argument arrived: a task that
        // ignores `filePath` still answers. These two tools can only produce the
        // asserted text by reading the path.
        var ct = TestContext.Current.CancellationToken;
        EngineBundlePath.RequireStaged();

        using var cache = IsolatedCacheRoot.Create();
        using var workspace = WorkspaceTestDirectoryFactory.Create();
        var mismatchedPath = Path.Combine(workspace.Path, "Gadget.cs");
        await File.WriteAllTextAsync(mismatchedPath, string.Empty, ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".editorconfig"), "root = true\n\n[*.cs]\nindent_size = 3\n", ct);

        await using var client = await StdioMcpServerClient.CreateFromBundleAsync(workspace.Path, cache.Path, ct);

        // Act
        var structure = await CallToolAsync(
            client,
            "analyze_csharp_project_structure",
            new() { ["content"] = "namespace Sample;\n\npublic sealed class Widget\n{\n}\n", ["filePath"] = mismatchedPath },
            ct);
        var editorconfig = await CallToolAsync(client, "read_editorconfig_rules", new() { ["filePath"] = mismatchedPath }, ct);

        // Assert
        Assert.Multiple(
            () => Assert.Contains("Gadget.cs", ReadText(structure), StringComparison.Ordinal),
            () => Assert.Contains("\"indent_size\":\"3\"", ReadText(editorconfig), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_not_report_a_rule_the_workspace_disabled()
    {
        // Arrange — the same source breaks two async rules; the workspace switches one off.
        var ct = TestContext.Current.CancellationToken;
        EngineBundlePath.RequireStaged();

        using var cache = IsolatedCacheRoot.Create();
        using var workspace = WorkspaceTestDirectoryFactory.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, ".autocontext.json"),
            """{ "instructions": { "dotnet-async-await.instructions.md": { "disabledRules": [ "INST0007" ] } } }""",
            ct);
        const string Source =
            "namespace Sample;\n\n/// <summary>S.</summary>\npublic sealed class Service\n{\n    /// <summary>R.</summary>\n    public async void Run() { }\n\n    /// <summary>L.</summary>\n    public async Task LoadAsync() { }\n}\n";

        await using var client = await StdioMcpServerClient.CreateFromBundleAsync(workspace.Path, cache.Path, ct);

        // Act
        var result = await CallToolAsync(client, "analyze_csharp_code_style", new() { ["content"] = Source }, ct);

        // Assert
        var text = ReadText(result);
        Assert.Multiple(
            () => Assert.DoesNotContain("dotnet-async-await#INST0007", text, StringComparison.Ordinal),
            () => Assert.Contains("dotnet-async-await#INST0002", text, StringComparison.Ordinal));
    }

    /// <summary>
    /// Calls one tool, retrying while the result is not the <c>ok</c> arm: the
    /// first call to each worker cold-spawns it and can lose the worker's
    /// accept re-arm race. Each attempt is a complete response, so retrying is
    /// safe; a tool that never answers with a report still fails.
    /// </summary>
    private static async Task<JsonMcpToolsInvokeResult> CallToolAsync(
        McpClient client,
        string name,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        const int MaxAttempts = 10;
        JsonMcpToolsInvokeResult? result = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var response = await client.CallToolAsync(name, arguments, cancellationToken: cancellationToken);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text;
            result = JsonSerializer.Deserialize(text, ProtocolJsonContext.Default.JsonMcpToolsInvokeResult);

            if (result is JsonMcpToolsInvokeOkResult)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return result!;
    }

    private static string Describe(JsonMcpToolsInvokeResult result)
        => JsonSerializer.Serialize(result, ProtocolJsonContext.Default.JsonMcpToolsInvokeResult);

    private static string ReadText(JsonMcpToolsInvokeResult result)
        => Assert.Single(Assert.IsType<JsonMcpToolsInvokeOkResult>(result).Content).GetProperty("text").GetString()!;

    private static HashSet<string> ReadRegisteredToolNames(string registryPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(registryPath));

        return document.RootElement
            .GetProperty("tools")
            .EnumerateArray()
            .Select(static tool => tool.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes a small, valid input for every shipped tool into
    /// <paramref name="workspacePath"/> and returns each tool's arguments,
    /// keyed by tool name.
    /// </summary>
    private static Dictionary<string, Dictionary<string, object?>> WriteFixtures(string workspacePath)
    {
        var sourcePath = Path.Combine(workspacePath, "src", "Widget.cs");
        const string Source = "namespace Sample;\n\n/// <summary>A widget.</summary>\npublic sealed class Widget\n{\n}\n";

        var testProjectDirectory = Path.Combine(workspacePath, "tests", "Sample.Tests");
        var testPath = Path.Combine(testProjectDirectory, "WidgetTests.cs");
        const string TestSource =
            "namespace Sample.Tests;\n\npublic sealed class WidgetTests\n{\n    [Fact]\n    public void Should_build_a_widget()\n    {\n        // Assert\n        Assert.True(true);\n    }\n}\n";

        const string ProjectFile =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Serilog\" Version=\"4.0.0\" />\n  </ItemGroup>\n</Project>\n";

        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(testProjectDirectory);
        File.WriteAllText(sourcePath, Source);
        File.WriteAllText(testPath, TestSource);
        File.WriteAllText(Path.Combine(testProjectDirectory, "Sample.Tests.csproj"), ProjectFile);
        File.WriteAllText(Path.Combine(workspacePath, ".editorconfig"), "root = true\n\n[*.cs]\nindent_size = 4\n");

        return new(StringComparer.Ordinal)
        {
            ["analyze_csharp_code_style"] = new() { ["content"] = Source, ["filePath"] = sourcePath },
            ["analyze_csharp_project_structure"] = new() { ["content"] = Source, ["filePath"] = sourcePath },
            ["analyze_csharp_testing_style"] = new()
            {
                ["content"] = TestSource,
                ["filePath"] = testPath,
                ["projectDirectory"] = testProjectDirectory,
            },
            ["analyze_nuget_references"] = new() { ["content"] = ProjectFile },
            ["analyze_git_commit_message"] = new() { ["content"] = "feat(widget): add the widget\n\nThe widget now exists.\n" },
            ["read_editorconfig_rules"] = new() { ["filePath"] = sourcePath },
            ["analyze_typescript_code_style"] = new() { ["content"] = "export const answer: number = 42;\n" },
        };
    }
}
