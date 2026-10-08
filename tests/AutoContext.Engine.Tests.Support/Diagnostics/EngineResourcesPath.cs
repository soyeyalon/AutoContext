namespace AutoContext.Engine.Tests.Support.Diagnostics;

/// <summary>
/// Resolves the absolute path to the side-car manifests shipped by the
/// <c>AutoContext.Engine</c> project (<c>src/AutoContext.Engine/Resources/</c>):
/// the hand-authored catalogs and registry, and the build-generated manifests.
/// Mirrors <see cref="EngineInstructionsPath"/>: the repository root is located by
/// searching upward from the test binary directory for the <c>AutoContext.slnx</c>
/// solution file, so the resolver does not depend on the exact number of
/// intermediate folders. Resolution is one-shot at class-load time.
/// </summary>
/// <remarks>
/// The build-time manifest generator reads <c>mcp-tools-registry.json</c> from this
/// directory to validate the corpus's tool obligations; pointing the validator at
/// it from a test proves the shipped corpus and the shipped registry agree without
/// running the build.
/// </remarks>
public static class EngineResourcesPath
{
    /// <summary>
    /// Absolute path to the engine's <c>Resources/</c> directory.
    /// The directory is not required to exist at resolution time; callers
    /// surface their own diagnostic when it is missing.
    /// </summary>
    public static string Value { get; } = Resolve();

    private static string Resolve()
    {
        var repoDir = RepositoryRoot.Value;

        return Path.Combine(repoDir, "src", "AutoContext.Engine", "Resources");
    }
}
