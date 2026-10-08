namespace AutoContext.Instructions.Manifest.Generator;

/// <summary>
/// One tool-obligation fault tied back to the corpus file and body line it was found
/// on: a <see cref="Token"/> inside a contracted tool section that the MCP tools
/// registry does not define. Mirrors
/// <see cref="InstructionsFileReferenceFindingEntry"/> so the build report can point
/// at the exact source the same way.
/// </summary>
/// <param name="SourceKey">The catalog key of the file the token was found in.</param>
/// <param name="SourceFileName">The file name of the file the token was found in.</param>
/// <param name="Kind">Why the token failed to resolve.</param>
/// <param name="Token">The inline-code token, verbatim without its backticks.</param>
/// <param name="Line">The zero-based line of the token within the body (with the
/// frontmatter removed).</param>
/// <param name="Message">A human-readable description of the fault.</param>
internal sealed record InstructionsFileToolObligationFindingEntry(
    string SourceKey,
    string SourceFileName,
    InstructionsFileToolObligationFindingKind Kind,
    string Token,
    int Line,
    string Message);
