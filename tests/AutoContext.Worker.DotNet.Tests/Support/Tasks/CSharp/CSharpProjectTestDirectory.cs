namespace AutoContext.Worker.DotNet.Tests.Support.Tasks.CSharp;

/// <summary>
/// A temporary directory holding one project file, so a check that reads the
/// project a source file belongs to sees a real one. Deleted on dispose.
/// </summary>
internal sealed class CSharpProjectTestDirectory : IDisposable
{
    public const string Library = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    public const string Console = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""";

    public const string Web = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    public const string Test = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit.v3" Version="3.2.2" /></ItemGroup></Project>""";

    private CSharpProjectTestDirectory(string root)
    {
        Root = root;
    }

    /// <summary>Gets the directory that holds the project file.</summary>
    public string Root { get; }

    /// <summary>Creates a directory holding <c>Sample.csproj</c> with
    /// <paramref name="projectXml"/>.</summary>
    public static CSharpProjectTestDirectory Create(string projectXml)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ac-csproj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Sample.csproj"), projectXml);

        return new CSharpProjectTestDirectory(root);
    }

    /// <summary>The absolute path a source file at
    /// <paramref name="relativePath"/> inside the project would have; the
    /// file itself is not written, because the checks receive its content.</summary>
    public string SourcePath(string relativePath = "Sample.cs")
        => Path.Combine(Root, relativePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
