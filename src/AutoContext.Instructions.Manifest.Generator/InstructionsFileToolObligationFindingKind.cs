namespace AutoContext.Instructions.Manifest.Generator;

using AutoContext.Instructions.Parser.Model;

/// <summary>
/// Why an inline-code token in a contracted tool section failed to resolve against
/// the MCP tools registry. These are cross-artefact faults — the corpus citing a
/// contract the registry does not define — so, like
/// <see cref="InstructionsFileReferenceFindingKind"/>, they surface only at build
/// time, once both artefacts are known.
/// </summary>
internal enum InstructionsFileToolObligationFindingKind
{
    /// <summary>A token in tool-name shape (<c>analyze_csharp_code</c>) names no
    /// <c>tools[].name</c> in the registry — a renamed or mistyped tool.</summary>
    UnknownTool,

    /// <summary>A token in parameter shape (<c>originalPath</c>) is not a declared
    /// parameter of any registered tool the same section names — a renamed or
    /// mistyped parameter, or a parameter cited without its tool.</summary>
    UnknownParameter,
}
