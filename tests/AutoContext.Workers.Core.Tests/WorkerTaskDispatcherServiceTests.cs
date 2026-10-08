namespace AutoContext.Workers.Core.Tests;

using System.IO.Pipes;
using System.Text.Json;

using AutoContext.Framework.Pipes;
using AutoContext.Workers.Core;
using AutoContext.Workers.Core.Tests.Support;

public sealed class WorkerTaskDispatcherServiceTests
{
    private static readonly string[] DisabledRules = ["lang-csharp#INST0015", "dotnet-xunit"];

    [Fact]
    public async Task Should_dispatch_request_to_matching_task_and_return_ok_envelope()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeName = $"ac-test-{Guid.NewGuid():N}";
        using var sut = WorkerTaskDispatcherServiceTestFactory.CreateService(pipeName, [new FakeEchoTask()]);
        await sut.StartAsync(cancellationToken);

        try
        {
            // Act
            var response = await WorkerDispatcherPipeTestClient.SendAsync(pipeName, new
            {
                mcpTask = "echo",
                data = new { value = 42 },
                editorconfig = new { },
            }, cancellationToken);

            // Assert
            Assert.Multiple(
                () => Assert.Equal("echo", response.GetProperty("mcpTask").GetString()),
                () => Assert.Equal("ok", response.GetProperty("status").GetString()),
                () => Assert.Equal(string.Empty, response.GetProperty("error").GetString()),
                () => Assert.Equal(42, response.GetProperty("output").GetProperty("value").GetInt32()));
        }
        finally
        {
            await sut.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Should_hand_the_disabled_rules_to_the_task()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeName = $"ac-test-{Guid.NewGuid():N}";
        using var sut = WorkerTaskDispatcherServiceTestFactory.CreateService(pipeName, [new FakeEchoTask()]);
        await sut.StartAsync(cancellationToken);

        try
        {
            // Act
            var response = await WorkerDispatcherPipeTestClient.SendAsync(pipeName, new
            {
                mcpTask = "echo",
                data = new { value = 42 },
                disabledRules = DisabledRules,
            }, cancellationToken);

            // Assert
            var output = response.GetProperty("output");
            Assert.Multiple(
                () => Assert.Equal(42, output.GetProperty("value").GetInt32()),
                () => Assert.Equal(
                    DisabledRules,
                    output.GetProperty("disabledRules").EnumerateArray().Select(static rule => rule.GetString())));
        }
        finally
        {
            await sut.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Should_return_error_envelope_for_unknown_task()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeName = $"ac-test-{Guid.NewGuid():N}";
        using var sut = WorkerTaskDispatcherServiceTestFactory.CreateService(pipeName, []);
        await sut.StartAsync(cancellationToken);

        try
        {
            // Act
            var response = await WorkerDispatcherPipeTestClient.SendAsync(pipeName, new
            {
                mcpTask = "does_not_exist",
                data = new { },
                editorconfig = new { },
            }, cancellationToken);

            // Assert
            Assert.Multiple(
                () => Assert.Equal("does_not_exist", response.GetProperty("mcpTask").GetString()),
                () => Assert.Equal("error", response.GetProperty("status").GetString()),
                () => Assert.Equal(JsonValueKind.Null, response.GetProperty("output").ValueKind),
                () => Assert.Contains("Unknown task", response.GetProperty("error").GetString(), StringComparison.Ordinal));
        }
        finally
        {
            await sut.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Should_return_error_envelope_when_task_throws()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeName = $"ac-test-{Guid.NewGuid():N}";
        using var sut = WorkerTaskDispatcherServiceTestFactory.CreateService(pipeName, [new FakeThrowingTask()]);
        await sut.StartAsync(cancellationToken);

        try
        {
            // Act
            var response = await WorkerDispatcherPipeTestClient.SendAsync(pipeName, new
            {
                mcpTask = "boom",
                data = new { },
                editorconfig = new { },
            }, cancellationToken);

            // Assert
            Assert.Multiple(
                () => Assert.Equal("error", response.GetProperty("status").GetString()),
                () => Assert.Contains("kaboom", response.GetProperty("error").GetString(), StringComparison.Ordinal));
        }
        finally
        {
            await sut.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Should_let_critical_exceptions_escape_dispatcher()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeName = $"ac-test-{Guid.NewGuid():N}";
        using var sut = WorkerTaskDispatcherServiceTestFactory.CreateService(pipeName, [new FakeCriticalThrowingTask()]);
        await sut.StartAsync(cancellationToken);

        try
        {
            // Act + Assert: a critical exception (e.g. OutOfMemoryException)
            // must NOT be converted into an error envelope. The dispatcher
            // re-throws, the connection drops without writing a response,
            // and the client observes a null read.
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000, cancellationToken);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    mcpTask = "critical_boom",
                    data = new { },
                    editorconfig = new { },
                },
                WorkerTaskDispatcherService.WorkerJsonOptions);
            var channel = new LengthPrefixedFrameCodec(client);
            await channel.WriteAsync(bytes, cancellationToken);

            var responseBytes = await channel.ReadAsync(cancellationToken);
            Assert.Null(responseBytes);
        }
        finally
        {
            await sut.StopAsync(cancellationToken);
        }
    }

}
