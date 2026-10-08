namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Workers.Core;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// <c>analyze_csharp_test_style</c> — enforces test style rules from
/// <c>dotnet-testing.instructions.md</c> and <c>dotnet-xunit.instructions.md</c>:
/// test class naming, test method naming, no XML doc comments,
/// <c>Assert.Multiple</c> for multi-assertion tests, no
/// <c>ConfigureAwait</c>, and namespace conformance to the standard .NET
/// convention (<c>RootNamespace</c> + folder path within the project).
/// </summary>
/// <remarks>
/// Request <c>data</c>:
/// <c>{ "content": "&lt;csharp-source&gt;", "filePath": "&lt;abs-path&gt;",
/// "projectDirectory": "&lt;abs-path&gt;", "rootNamespace": "&lt;namespace&gt;" }</c><br/>
/// <c>filePath</c> is the engine's name for the test file's path; the legacy
/// MCP server still sends it as <c>comparedPath</c>, which is read when
/// <c>filePath</c> is absent until that server retires.<br/>
/// <c>rootNamespace</c> is optional — when omitted the analyzer derives it
/// from the first <c>*.csproj</c> file found in <c>projectDirectory</c>
/// (filename without extension), matching MSBuild's default behaviour.<br/>
/// Response <c>output</c>: <c>{ "passed": &lt;bool&gt;, "report": "&lt;text&gt;" }</c>
/// </remarks>
internal sealed class AnalyzeCSharpTestStyleTask : IMcpTask
{
    public string TaskName => "analyze_csharp_test_style";

    public async Task<JsonElement> ExecuteAsync(JsonElement data, CancellationToken cancellationToken)
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

        var comparedPath = data.TryGetString("filePath") ?? data.TryGetString("comparedPath") ?? string.Empty;
        var projectDirectory = data.TryGetString("projectDirectory") ?? string.Empty;
        var rootNamespace = data.TryGetString("rootNamespace") ?? string.Empty;

        var (passed, report) = await BuildReportAsync(content, comparedPath, projectDirectory, rootNamespace, cancellationToken).ConfigureAwait(false);

        var output = new JsonObject
        {
            ["passed"] = passed,
            ["report"] = report,
        };

        return JsonSerializer.SerializeToElement(output);
    }

    private static async Task<(bool Passed, string Report)> BuildReportAsync(
        string content,
        string comparedPath,
        string projectDirectory,
        string rootNamespace,
        CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var violations = new List<string>();

        var testClasses = FindTestClasses(root);

        if (testClasses.Count == 0)
        {
            return (true, "✅ Test style is correct.");
        }

        var fileName = string.IsNullOrEmpty(comparedPath) ? string.Empty : Path.GetFileName(comparedPath);
        AnalyzeFileNameConvention(fileName, violations);
        AnalyzeNamespaceConvention(root, comparedPath, projectDirectory, rootNamespace, violations);

        foreach (var testClass in testClasses)
        {
            AnalyzeTestClassNaming(testClass, tree, violations);
            AnalyzeTestClassXmlDoc(testClass, tree, violations);

            var testMethods = GetTestMethods(testClass);

            foreach (var method in testMethods)
            {
                AnalyzeTestMethodNaming(method, tree, violations);
                AnalyzeTestMethodXmlDoc(method, tree, violations);
                AnalyzeAssertMultiple(method, tree, violations);
                AnalyzeConfigureAwait(method, tree, violations);
            }
        }

        if (violations.Count == 0)
        {
            return (true, "✅ Test style is correct.");
        }

        var report = $"❌ Found {violations.Count} test style violation(s):\n" +
                     string.Join('\n', violations.Select((v, i) => $"  {i + 1}. {v}"));

        return (false, report);
    }

    // [dotnet-testing INST0005]: file name follows class name '<UnitUnderTest>Tests'
    private static void AnalyzeFileNameConvention(ReadOnlySpan<char> fileName, List<string> violations)
    {
        if (fileName.IsEmpty || fileName.IsWhiteSpace())
        {
            return;
        }

        var nameWithoutExtension = StripAllExtensions(fileName);

        if (!nameWithoutExtension.EndsWith("Tests", StringComparison.Ordinal))
        {
            violations.Add(
                $"Test file '{fileName}' must end with 'Tests' before the extension " +
                $"(e.g., '{nameWithoutExtension}Tests.cs').");
        }
    }

    /// <summary>
    /// Strips all extensions from a file name (e.g., "FooTests.razor.cs" → "FooTests").
    /// </summary>
    private static ReadOnlySpan<char> StripAllExtensions(ReadOnlySpan<char> fileName)
    {
        var name = Path.GetFileName(fileName);
        var dotIndex = name.IndexOf('.');

        return dotIndex < 0 ? name : name[..dotIndex];
    }

    // [dotnet-testing INST0001]: namespace follows project structure (RootNamespace + folder path)
    private static void AnalyzeNamespaceConvention(
        SyntaxNode root,
        string comparedPath,
        string projectDirectory,
        string rootNamespace,
        List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(comparedPath) || string.IsNullOrWhiteSpace(projectDirectory))
        {
            return;
        }

        var declaredNamespace = GetDeclaredNamespace(root);

        if (declaredNamespace is null)
        {
            return;
        }

        var resolvedRoot = string.IsNullOrWhiteSpace(rootNamespace)
            ? DeriveRootNamespaceFromProjectFile(projectDirectory)
            : rootNamespace;

        if (string.IsNullOrWhiteSpace(resolvedRoot))
        {
            return;
        }

        var expectedNamespace = ComputeExpectedNamespace(resolvedRoot, projectDirectory, comparedPath);

        if (expectedNamespace is null)
        {
            return;
        }

        if (!string.Equals(declaredNamespace, expectedNamespace, StringComparison.Ordinal))
        {
            violations.Add(
                $"Namespace '{declaredNamespace}' does not match the project structure. " +
                $"Expected '{expectedNamespace}' (RootNamespace + folder path).");
        }
    }

    private static string? DeriveRootNamespaceFromProjectFile(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            return null;
        }

        var projectFile = Directory
            .EnumerateFiles(projectDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        return projectFile is null
            ? null
            : Path.GetFileNameWithoutExtension(projectFile);
    }

    private static string? ComputeExpectedNamespace(string rootNamespace, string projectDirectory, string comparedPath)
    {
        var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(comparedPath));

        if (fileDirectory is null)
        {
            return null;
        }

        var projectDir = Path.GetFullPath(projectDirectory);
        var relative = Path.GetRelativePath(projectDir, fileDirectory);

        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            // File lives outside the declared project directory \u2014 cannot enforce.
            return null;
        }
        if (relative == ".")
        {
            return rootNamespace;
        }
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 0
            ? rootNamespace
            : $"{rootNamespace}.{string.Join('.', segments)}";
    }

    private static string? GetDeclaredNamespace(SyntaxNode root)
    {
        var fileScopedNs = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

        if (fileScopedNs is not null)
        {
            return fileScopedNs.Name.ToString();
        }

        var blockNs = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();

        return blockNs?.Name.ToString();
    }

    private static List<TypeDeclarationSyntax> FindTestClasses(SyntaxNode root)
        => [.. root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(TestDetection.IsTestClass)];

    private static List<MethodDeclarationSyntax> GetTestMethods(TypeDeclarationSyntax testClass)
        => [.. testClass.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(TestDetection.HasTestAttribute)];

    // [dotnet-testing INST0002]: test class suffix Tests
    private static void AnalyzeTestClassNaming(TypeDeclarationSyntax testClass, SyntaxTree tree, List<string> violations)
    {
        var name = testClass.Identifier.Text;

        if (!name.EndsWith("Tests", StringComparison.Ordinal))
        {
            var line = tree.GetLineSpan(testClass.Identifier.Span).StartLinePosition.Line + 1;
            violations.Add(
                $"Line {line}: Test class '{name}' must be suffixed with 'Tests' " +
                $"(e.g., '{name}Tests').");
        }
    }

    // [dotnet-testing INST0008]: no XML docs on test classes
    private static void AnalyzeTestClassXmlDoc(TypeDeclarationSyntax testClass, SyntaxTree tree, List<string> violations)
    {
        if (!HasXmlDocComment(testClass))
        {
            return;
        }

        var line = tree.GetLineSpan(testClass.Identifier.Span).StartLinePosition.Line + 1;
        violations.Add(
            $"Line {line}: Test class '{testClass.Identifier.Text}' should not have XML doc comments. " +
            "Rely on descriptive names to convey intent.");
    }

    // [dotnet-testing INST0002]: test method prefix Should_ / Should_not_
    private static void AnalyzeTestMethodNaming(MethodDeclarationSyntax method, SyntaxTree tree, List<string> violations)
    {
        var name = method.Identifier.Text;

        if (!name.StartsWith("Should_", StringComparison.Ordinal))
        {
            var line = tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line + 1;
            violations.Add(
                $"Line {line}: Test method '{name}' must start with 'Should_' or 'Should_not_' " +
                $"(e.g., 'Should_{ToSnakeCase(name)}').");
        }
    }

    // [dotnet-testing INST0008]: no XML docs on test methods
    private static void AnalyzeTestMethodXmlDoc(MethodDeclarationSyntax method, SyntaxTree tree, List<string> violations)
    {
        if (!HasXmlDocComment(method))
        {
            return;
        }

        var line = tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line + 1;
        violations.Add(
            $"Line {line}: Test method '{method.Identifier.Text}' should not have XML doc comments. " +
            "Rely on descriptive names to convey intent.");
    }

    // [xunit INST0004]: Assert.Multiple for multiple assertions
    private static void AnalyzeAssertMultiple(MethodDeclarationSyntax method, SyntaxTree tree, List<string> violations)
    {
        if (method.Body is null && method.ExpressionBody is null)
        {
            return;
        }

        var assertCalls = GetAssertCalls(method).ToList();

        if (assertCalls.Count <= 1)
        {
            return;
        }

        // If all Assert calls are already inside an Assert.Multiple lambda, it's fine.
        var hasAssertMultiple = assertCalls.Any(call =>
            call is { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Multiple" } });

        if (hasAssertMultiple)
        {
            return;
        }

        var line = tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line + 1;
        violations.Add(
            $"Line {line}: Test method '{method.Identifier.Text}' has {assertCalls.Count} Assert calls " +
            "but does not use Assert.Multiple(). Wrap multiple assertions in Assert.Multiple() " +
            "so all failures are reported together.");
    }

    // [xunit INST0009]: no .ConfigureAwait() in test methods
    private static void AnalyzeConfigureAwait(MethodDeclarationSyntax method, SyntaxTree tree, List<string> violations)
    {
        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            if (!string.Equals(memberAccess.Name.Identifier.Text, "ConfigureAwait", StringComparison.Ordinal))
            {
                continue;
            }

            var line = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
            violations.Add(
                $"Line {line}: Test method '{method.Identifier.Text}' calls .ConfigureAwait(). " +
                "Do not call .ConfigureAwait() inside test methods (xUnit1030).");
        }
    }

    private static IEnumerable<InvocationExpressionSyntax> GetAssertCalls(MethodDeclarationSyntax method)
        => method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "Assert" },
            });

    private static bool HasXmlDocComment(SyntaxNode node)
        => node.GetLeadingTrivia()
            .Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                      || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var result = new StringBuilder();
        result.Append(char.ToLowerInvariant(name[0]));

        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                result.Append('_');
                result.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                result.Append(name[i]);
            }
        }

        return result.ToString();
    }
}
