namespace AutoContext.Engine.Tests.Support.Mcp;

using AutoContext.Engine.Tests.Support.Diagnostics;

using ModelContextProtocol.Client;

/// <summary>
/// Spawns the <c>autocontext-engine</c> binary in the
/// <c>--mcp-server with-stdio</c> role and drives it through an
/// <see cref="McpClient"/> over real stdio. The cross-process companion to
/// <see cref="Pipes.EngineWireTestClient"/>: that helper dials the daemon
/// role's pipes, this one speaks the MCP JSON-RPC protocol over the child's
/// stdin/stdout the way a host (VS Code, Claude Code) would.
/// </summary>
/// <remarks>
/// The MCP role mints its own ephemeral instance id and binds no daemon
/// pipes, so — unlike <see cref="EngineTestProcess"/> — there is
/// no readiness pipe to probe: <see cref="StdioClientTransport"/> owns the
/// child process, and <see cref="McpClient.CreateAsync"/> completing the
/// <c>initialize</c> handshake is itself the readiness signal.
/// </remarks>
public static class StdioMcpServerClient
{
    /// <summary>
    /// Builds the argv for the <c>--mcp-server with-stdio</c> role: the
    /// required <c>--workspace</c> plus the role selector, and the optional
    /// side-car / cache overrides. Omits every daemon-only switch, which the
    /// role rejects.
    /// </summary>
    /// <param name="workspacePath">Absolute workspace path.</param>
    /// <param name="resourcesRootOverride">Optional <c>--resources-root</c>
    /// overlay; <see langword="null"/> uses the bundled side-cars.</param>
    /// <param name="cacheRootOverride">Optional <c>--cache-root</c> override;
    /// <see langword="null"/> uses the default cache root.</param>
    /// <returns>The argument list, in argv order.</returns>
    public static IReadOnlyList<string> BuildArguments(
        string workspacePath,
        string? resourcesRootOverride = null,
        string? cacheRootOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var arguments = new List<string>
        {
            "--workspace",
            workspacePath,
            "--mcp-server",
            "with-stdio",
        };

        if (resourcesRootOverride is not null)
        {
            arguments.Add("--resources-root");
            arguments.Add(resourcesRootOverride);
        }

        if (cacheRootOverride is not null)
        {
            arguments.Add("--cache-root");
            arguments.Add(cacheRootOverride);
        }

        return arguments;
    }

    /// <summary>
    /// Spawns the engine in the stdio MCP-server role and returns a connected
    /// <see cref="McpClient"/> whose <c>initialize</c> handshake has already
    /// completed. Dispose the client to close stdin and reap the process.
    /// </summary>
    /// <param name="workspacePath">Absolute workspace path.</param>
    /// <param name="resourcesRootOverride">Optional <c>--resources-root</c>
    /// overlay; <see langword="null"/> uses the bundled side-cars.</param>
    /// <param name="cacheRootOverride">Optional <c>--cache-root</c> override;
    /// <see langword="null"/> uses the default cache root.</param>
    /// <param name="cancellationToken">Token that bounds process spawn and the
    /// initialize handshake.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="FileNotFoundException">The engine binary has not been
    /// built.</exception>
    public static async Task<McpClient> CreateAsync(
        string workspacePath,
        string? resourcesRootOverride,
        string? cacheRootOverride,
        CancellationToken cancellationToken)
    {
        var executablePath = EngineBinaryPath.Value;
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "autocontext-engine binary not found. Run '.\\build.ps1 DotNet' before running engine integration tests.",
                executablePath);
        }

        return await ConnectAsync(
            executablePath,
            BuildArguments(workspacePath, resourcesRootOverride, cacheRootOverride),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Spawns the <em>staged bundle's</em> engine in the stdio MCP-server role
    /// — the shipped layout, with its own side-cars and real workers — and
    /// returns a connected <see cref="McpClient"/>. Use this to prove what a
    /// host actually gets; <see cref="CreateAsync"/> runs the build-output
    /// binary, whose workers are not staged beside it.
    /// </summary>
    /// <param name="workspacePath">Absolute workspace path.</param>
    /// <param name="cacheRootOverride">Optional <c>--cache-root</c> override;
    /// <see langword="null"/> uses the default cache root.</param>
    /// <param name="cancellationToken">Token that bounds process spawn and the
    /// initialize handshake.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="DirectoryNotFoundException">The bundle has not been
    /// staged.</exception>
    public static async Task<McpClient> CreateFromBundleAsync(
        string workspacePath,
        string? cacheRootOverride,
        CancellationToken cancellationToken)
    {
        EngineBundlePath.RequireStaged();

        return await ConnectAsync(
            EngineBundlePath.Executable,
            BuildArguments(workspacePath, resourcesRootOverride: null, cacheRootOverride),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<McpClient> ConnectAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "autocontext-engine (mcp-server smoke)",
            Command = executablePath,
            Arguments = [.. arguments],
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
