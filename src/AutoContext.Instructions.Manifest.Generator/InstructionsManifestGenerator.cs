namespace AutoContext.Instructions.Manifest.Generator;

using System.Text;

using AutoContext.Instructions.Parser.Model;

using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates one build-time generation pass: parses the corpus once via
/// <see cref="ICorpusParser"/>, cross-validates the hand-authored catalog against
/// it via <see cref="IInstructionsCatalogReader"/>, reads the hand-authored MCP
/// tools registry via <see cref="IMcpToolsRegistryReader"/>, builds the generated
/// fact manifest via <see cref="IInstructionsManifestBuilder"/>, validates
/// cross-file references via <see cref="IInstructionsReferenceValidator"/> and the
/// corpus's tool obligations against the registry via
/// <see cref="IInstructionsToolObligationValidator"/>, serialises the manifest, and
/// writes it to disk only when the bytes differ from the file already there. The
/// generator owns the process exit-code contract the MSBuild <c>&lt;Exec&gt;</c>
/// caller observes: <c>0</c> on success, <c>1</c> on a curatorial, catalog,
/// registry, reference, or tool-obligation fault, <c>2</c> on a usage error.
/// </summary>
internal sealed partial class InstructionsManifestGenerator(
    ICorpusParser corpusParser,
    IInstructionsCatalogReader catalogReader,
    IMcpToolsRegistryReader registryReader,
    IInstructionsManifestBuilder builder,
    IInstructionsManifestSerializer manifestSerializer,
    IInstructionsReferenceValidator referenceValidator,
    IInstructionsToolObligationValidator toolObligationValidator,
    ILogger<InstructionsManifestGenerator> logger)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Runs the generation pass described by the positional
    /// <paramref name="args"/>: <c>[corpus-directory, catalog-json-path,
    /// registry-json-path, manifest-json-path]</c> — the three inputs, then the
    /// one output.
    /// </summary>
    /// <param name="args">The positional command-line arguments.</param>
    /// <param name="cancellationToken">Cancels the generation pass.</param>
    /// <returns>The process exit code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is
    /// <see langword="null"/>.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count != 4)
        {
            LogUsage(logger);
            return 2;
        }

        var corpusDirectory = args[0];
        var catalogPath = args[1];
        var registryPath = args[2];
        var manifestOutputPath = args[3];

        try
        {
            var corpus = await corpusParser.ParseAsync(corpusDirectory, cancellationToken).ConfigureAwait(false);
            _ = catalogReader.Read(catalogPath, corpus);
            var registry = registryReader.Read(registryPath);
            var manifest = builder.Build(corpus);

            // Both validators run so one build report lists every fault, not just the first family.
            var hasReferenceFault = HasReferenceFault(corpus);
            var hasToolObligationFault = HasToolObligationFault(corpus, registry);

            if (hasReferenceFault || hasToolObligationFault)
            {
                return 1;
            }

            WriteIfChanged(manifestOutputPath, manifestSerializer.Serialize(manifest));

            return 0;
        }
        catch (InvalidOperationException exception)
        {
            LogCuratorialFault(logger, exception.Message);
            return 1;
        }
    }

    private static string Describe(InstructionsFileReferenceFindingEntry finding)
        => $"{finding.SourceFileName} (body line {finding.Failure.Reference.Line + 1}): {finding.Failure.Message}";

    private static string Describe(InstructionsFileToolObligationFindingEntry finding)
        => $"{finding.SourceFileName} (body line {finding.Line + 1}): {finding.Message}";

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "{Message}")]
    private static partial void LogCuratorialFault(ILogger logger, string message);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "{Message}")]
    private static partial void LogReferenceFault(ILogger logger, string message);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "{Message}")]
    private static partial void LogReferenceWarning(ILogger logger, string message);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Error,
        Message = "{Message}")]
    private static partial void LogToolObligationFault(ILogger logger, string message);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "usage: instructions-manifest-gen <corpus-directory> <catalog-json-path> <registry-json-path> <manifest-json-path>")]
    private static partial void LogUsage(ILogger logger);

    private static void WriteIfChanged(string outputPath, string json)
    {
        var directory = Path.GetDirectoryName(outputPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputPath) && File.ReadAllText(outputPath, Utf8NoBom) == json)
        {
            return;
        }

        File.WriteAllText(outputPath, json, Utf8NoBom);
    }

    /// <summary>
    /// Aggregates every cross-file reference finding, logging redundant locators as
    /// warnings (they ship) and the resolution failures as errors (they fail the build).
    /// </summary>
    /// <returns><see langword="true"/> when at least one fatal reference fault was found.</returns>
    private bool HasReferenceFault(IReadOnlyDictionary<string, InstructionsFileParsedFile> corpus)
    {
        var hasFatal = false;

        foreach (var finding in referenceValidator.Validate(corpus))
        {
            if (finding.Failure.Kind == InstructionsFileReferenceFindingKind.RedundantLocator)
            {
                LogReferenceWarning(logger, Describe(finding));
            }
            else
            {
                hasFatal = true;
                LogReferenceFault(logger, Describe(finding));
            }
        }

        return hasFatal;
    }

    /// <summary>
    /// Aggregates every tool-obligation finding as an error. There is no warning
    /// tier: a tool or parameter the registry does not define is prose the model
    /// cannot act on, so every finding fails the build.
    /// </summary>
    /// <returns><see langword="true"/> when at least one finding was reported.</returns>
    private bool HasToolObligationFault(
        IReadOnlyDictionary<string, InstructionsFileParsedFile> corpus,
        IReadOnlyDictionary<string, McpToolsRegistryTool> registry)
    {
        var hasFault = false;

        foreach (var finding in toolObligationValidator.Validate(corpus, registry))
        {
            hasFault = true;
            LogToolObligationFault(logger, Describe(finding));
        }

        return hasFault;
    }
}
