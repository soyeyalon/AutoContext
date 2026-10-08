namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Classifies the project a C# file belongs to by reading the nearest
/// <c>*.csproj</c> above it. Checks use the answer to apply a rule only where
/// it holds — <c>ConfigureAwait(false)</c>, for example, matters in a library
/// that may run under a synchronization context, and is noise in an
/// application or a test.
/// </summary>
/// <remarks>
/// Only the nearest project file is read; properties inherited from
/// <c>Directory.Build.props</c> are not followed. A test project is
/// recognised by <c>IsTestProject</c> or by a test-framework package, and is
/// checked first because xUnit v3 test projects are also executables.
/// </remarks>
internal static class CSharpProjectKindResolver
{
    private static readonly HashSet<string> ApplicationSdks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Sdk.Web",
        "Microsoft.NET.Sdk.Worker",
        "Microsoft.NET.Sdk.BlazorWebAssembly",
    };

    private static readonly HashSet<string> TestPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk",
        "xunit",
        "xunit.v3",
        "NUnit",
        "MSTest",
        "MSTest.TestFramework",
    };

    /// <summary>
    /// Finds and classifies the project that contains <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">The absolute path of a C# file, or
    /// <see langword="null"/> when unknown.</param>
    /// <returns>The project kind, or <see cref="CSharpProjectKind.Unknown"/>
    /// when the path is missing or relative, no project file is found, or the
    /// project file cannot be read.</returns>
    public static CSharpProjectKind Resolve(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return CSharpProjectKind.Unknown;
        }

        var directory = Path.GetDirectoryName(filePath);

        while (!string.IsNullOrEmpty(directory))
        {
            if (Directory.Exists(directory))
            {
                var project = Directory
                    .EnumerateFiles(directory, "*.csproj")
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();

                if (project is not null)
                {
                    return ClassifyFile(project);
                }
            }

            directory = Path.GetDirectoryName(directory);
        }

        return CSharpProjectKind.Unknown;
    }

    /// <summary>
    /// Classifies a parsed project file.
    /// </summary>
    /// <param name="project">The project document.</param>
    /// <returns>The project kind; <see cref="CSharpProjectKind.Library"/> when
    /// nothing marks it as a test or an application.</returns>
    public static CSharpProjectKind Classify(XDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var root = project.Root;

        if (root is null)
        {
            return CSharpProjectKind.Unknown;
        }

        if (IsTestProject(root))
        {
            return CSharpProjectKind.Test;
        }

        var outputType = ReadProperty(root, "OutputType");

        if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)
            || ReadSdks(root).Any(ApplicationSdks.Contains))
        {
            return CSharpProjectKind.Application;
        }

        return CSharpProjectKind.Library;
    }

    private static CSharpProjectKind ClassifyFile(string projectPath)
    {
        try
        {
            return Classify(XDocument.Load(projectPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return CSharpProjectKind.Unknown;
        }
    }

    private static bool IsTestProject(XElement root)
        => string.Equals(ReadProperty(root, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
           || root.Descendants()
               .Where(static element => element.Name.LocalName == "PackageReference")
               .Select(static element => (string?)element.Attribute("Include"))
               .Any(static include => include is not null && TestPackages.Contains(include));

    private static string? ReadProperty(XElement root, string name)
        => root.Descendants()
            .Where(element => element.Name.LocalName == name && element.Parent?.Name.LocalName == "PropertyGroup")
            .Select(static element => element.Value.Trim())
            .LastOrDefault();

    /// <summary>
    /// The SDK names a project declares, from the root <c>Sdk</c> attribute
    /// (semicolon-separated, optionally <c>Name/Version</c>) and from
    /// <c>&lt;Sdk Name="…" /&gt;</c> elements.
    /// </summary>
    private static IEnumerable<string> ReadSdks(XElement root)
    {
        var fromAttribute = ((string?)root.Attribute("Sdk") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var fromElements = root.Elements()
            .Where(static element => element.Name.LocalName == "Sdk")
            .Select(static element => (string?)element.Attribute("Name"))
            .OfType<string>();

        return fromAttribute
            .Concat(fromElements)
            .Select(static sdk => sdk.Split('/')[0].Trim());
    }
}
