namespace AutoContext.Instructions.Manifest.Generator;

/// <summary>
/// Proves that every tool obligation the corpus states is one the MCP tools
/// registry defines. An instruction file's <c>## MCP Tool Validation</c> section
/// (and the always-attached <c>## Workflow MCP Tools Triggers</c> table) is prose
/// the model acts on: it names a tool to call and the parameters to pass. The
/// registry is the only place those names are defined, so a token in tool-name
/// shape must be a registered tool and a token in parameter shape must be a
/// parameter of a tool the same section names. Every other inline-code token —
/// file extensions, commands, element names — is outside the contract by shape and
/// never reported. Findings are collected rather than thrown so one build report
/// can list them all; deciding that any finding fails the build is the caller's
/// concern.
/// </summary>
internal interface IInstructionsToolObligationValidator
{
    /// <summary>
    /// Validates every contracted section in <paramref name="corpus"/> against
    /// <paramref name="registry"/>.
    /// </summary>
    /// <param name="corpus">The parsed corpus, keyed by basename stem.</param>
    /// <param name="registry">The registered tools, keyed by name.</param>
    /// <returns>Every unresolved tool or parameter token, ordered by source file
    /// then by body position; empty when the corpus and the registry agree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="corpus"/> or
    /// <paramref name="registry"/> is <see langword="null"/>.</exception>
    IReadOnlyList<InstructionsFileToolObligationFindingEntry> Validate(
        IReadOnlyDictionary<string, InstructionsFileParsedFile> corpus,
        IReadOnlyDictionary<string, McpToolsRegistryTool> registry);
}
