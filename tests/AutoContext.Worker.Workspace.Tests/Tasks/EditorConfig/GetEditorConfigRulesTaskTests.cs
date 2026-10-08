namespace AutoContext.Worker.Workspace.Tests.Tasks.EditorConfig;

using AutoContext.Framework.Tests.Support.Workers;
using AutoContext.Worker.Workspace.Tasks.EditorConfig;
using AutoContext.Worker.Workspace.Tests.Support.Shared;

public sealed class GetEditorConfigRulesTaskTests : IDisposable
{
    private static readonly string[] FilteredKeys = ["indent_style", "missing_key"];

    private readonly TempDirectoryFixture _workspace = new("ac-worker-tests");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Should_filter_to_requested_keys_and_omit_missing()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await _workspace.WriteFileAsync(
            ".editorconfig",
            "root = true\n\n[*.cs]\nindent_style = space\nindent_size = 4\n",
            cancellationToken);
        var filePath = await _workspace.WriteFileAsync("Foo.cs", string.Empty, cancellationToken);

        // Act
        var output = await new GetEditorConfigRulesTask().ExecuteAsync(new
        {
            path = filePath,
            keys = FilteredKeys,
        });

        // Assert
        Assert.Multiple(
            () => Assert.Equal("space", output.GetProperty("indent_style").GetString()),
            () => Assert.False(output.TryGetProperty("missing_key", out _)),
            () => Assert.False(output.TryGetProperty("indent_size", out _)));
    }

    [Fact]
    public async Task Should_return_all_keys_when_keys_filter_absent()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await _workspace.WriteFileAsync(".editorconfig", "root = true\n\n[*.cs]\nindent_style = tab\n", cancellationToken);
        var filePath = await _workspace.WriteFileAsync("Foo.cs", string.Empty, cancellationToken);

        // Act
        var output = await new GetEditorConfigRulesTask().ExecuteAsync(new { path = filePath });

        // Assert
        Assert.Equal("tab", output.GetProperty("indent_style").GetString());
    }

    [Fact]
    public async Task Should_resolve_the_engine_file_path_argument()
    {
        // Arrange — the engine's read_editorconfig_rules tool passes `filePath`, not `path`.
        var cancellationToken = TestContext.Current.CancellationToken;
        await _workspace.WriteFileAsync(".editorconfig", "root = true\n\n[*.cs]\nindent_size = 2\n", cancellationToken);
        var filePath = await _workspace.WriteFileAsync("Foo.cs", string.Empty, cancellationToken);

        // Act
        var output = await new GetEditorConfigRulesTask().ExecuteAsync(new { filePath });

        // Assert
        Assert.Equal("2", output.GetProperty("indent_size").GetString());
    }

    [Fact]
    public async Task Should_throw_when_data_path_missing()
    {
        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GetEditorConfigRulesTask().ExecuteAsync(new { }));
    }
}
