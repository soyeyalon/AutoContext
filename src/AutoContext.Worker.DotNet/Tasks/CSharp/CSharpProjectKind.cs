namespace AutoContext.Worker.DotNet.Tasks.CSharp;

/// <summary>
/// What kind of project a C# file belongs to, as far as the checks need to
/// know: some rules only make sense for code other projects consume.
/// </summary>
internal enum CSharpProjectKind
{
    /// <summary>No project file was found, or the file's path is unknown.</summary>
    Unknown,

    /// <summary>A class library: code other projects consume.</summary>
    Library,

    /// <summary>An executable, web, or worker-service application.</summary>
    Application,

    /// <summary>A test project.</summary>
    Test,
}
