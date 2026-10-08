namespace AutoContext.Worker.DotNet.Tasks.NuGet;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

using AutoContext.Workers.Core;
using AutoContext.Workers.Core.Analysis;

/// <summary>
/// <c>analyze_nuget_hygiene</c> — enforces NuGet hygiene rules from
/// <c>dotnet-nuget.instructions.md</c>: no duplicate references, no floating
/// versions, no missing versions (unless Central Package Management), and
/// flags packages that have well-known built-in .NET alternatives.
/// </summary>
/// <remarks>
/// Request <c>data</c>:  <c>{ "content": "&lt;csproj-xml&gt;" }</c><br/>
/// Response <c>output</c>: <c>{ "passed": &lt;bool&gt;, "report": "&lt;markdown&gt;" }</c>
/// </remarks>
internal sealed class AnalyzeNuGetHygieneTask : IMcpTask
{
    /// <summary>
    /// Maps package names (case-insensitive) to their built-in .NET alternative.
    /// </summary>
    private static readonly Dictionary<string, string> BuiltInAlternatives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Newtonsoft.Json"] = "System.Text.Json",
        ["AutoMapper"] = "manual mapping or Mapster",
        ["FluentValidation"] = "System.ComponentModel.DataAnnotations or custom validation",
        ["MediatR"] = "built-in DI and direct service calls",
        ["Polly"] = "Microsoft.Extensions.Http.Resilience (for .NET 8+)",
        ["RestSharp"] = "System.Net.Http.HttpClient",
        ["Dapper"] = "Entity Framework Core or ADO.NET",
    };

    public string TaskName => "analyze_nuget_hygiene";

    public Task<JsonElement> ExecuteAsync(JsonElement data, CancellationToken cancellationToken)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("content", out var contentElement)
            || contentElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("'data.content' is required and must be a string.");
        }

        var content = contentElement.GetString()!;

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("'data.content' must not be empty or whitespace.");
        }

        XDocument doc;

        try
        {
            doc = XDocument.Parse(content);
        }
        catch (XmlException ex)
        {
            return Task.FromResult(UnparseableOutput($"❌ Failed to parse .csproj XML: {ex.Message}"));
        }

        if (doc.Root is null)
        {
            return Task.FromResult(UnparseableOutput("❌ Failed to parse .csproj XML: document has no root element."));
        }

        var findings = new AnalyzerFindings();
        var packages = GetPackageReferences(doc.Root);
        var usesCpm = UsesCentralPackageManagement(doc.Root);

        CheckDuplicatePackages(packages, findings);
        CheckFloatingVersions(packages, findings);
        CheckMissingVersions(packages, usesCpm, findings);
        CheckBuiltInAlternatives(packages, findings);

        return Task.FromResult(findings.ToOutput("NuGet hygiene is correct.", "NuGet hygiene", data));
    }

    /// <summary>
    /// The output for a project file that cannot be read at all: a failed
    /// check with no findings, because no rule was evaluated.
    /// </summary>
    private static JsonElement UnparseableOutput(string report)
        => JsonSerializer.SerializeToElement(new JsonObject
        {
            ["passed"] = false,
            ["report"] = report,
            ["findings"] = new JsonArray(),
        });

    private static List<(string Name, string? Version)> GetPackageReferences(XElement root)
    {
        var packages = new List<(string Name, string? Version)>();

        foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var name = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Version can be an attribute or a child element.
            var version = element.Attribute("Version")?.Value
                          ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

            packages.Add((name, version));
        }

        return packages;
    }

    private static bool UsesCentralPackageManagement(XElement root)
        => root.Descendants()
            .Any(e => e.Name.LocalName == "ManagePackageVersionsCentrally"
                      && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    // review package references — no duplicates
    private static void CheckDuplicatePackages(
        List<(string Name, string? Version)> packages,
        AnalyzerFindings findings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, _) in packages)
        {
            if (!seen.Add(name))
            {
                findings.Add("dotnet-nuget#INST0002", null, $"Duplicate PackageReference '{name}'. Remove the redundant entry.");
            }
        }
    }

    // review package references — no floating versions
    private static void CheckFloatingVersions(
        List<(string Name, string? Version)> packages,
        AnalyzerFindings findings)
    {
        foreach (var (name, version) in packages)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (version.Contains('*', StringComparison.Ordinal)
                || version.Contains('[', StringComparison.Ordinal)
                || version.Contains('(', StringComparison.Ordinal))
            {
                findings.Add("dotnet-nuget#INST0002", null,
                    $"Package '{name}' uses a floating or range version '{version}'. " +
                    "Pin to an exact version for reproducible builds.");
            }
        }
    }

    // review package references — no missing versions
    private static void CheckMissingVersions(
        List<(string Name, string? Version)> packages,
        bool usesCpm,
        AnalyzerFindings findings)
    {
        if (usesCpm)
        {
            return;
        }

        foreach (var (name, version) in packages)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                findings.Add("dotnet-nuget#INST0002", null,
                    $"Package '{name}' has no Version specified. " +
                    "Add an explicit version or enable Central Package Management.");
            }
        }
    }

    // prefer built-in .NET libraries
    private static void CheckBuiltInAlternatives(
        List<(string Name, string? Version)> packages,
        AnalyzerFindings findings)
    {
        foreach (var (name, _) in packages)
        {
            if (BuiltInAlternatives.TryGetValue(name, out var alternative))
            {
                findings.Add("dotnet-nuget#INST0001", null,
                    $"Package '{name}' has a built-in .NET alternative: {alternative}. " +
                    "Consider whether the built-in option meets your needs before keeping this dependency.");
            }
        }
    }
}
