namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Workers.Core;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// <c>analyze_csharp_project_structure</c> — enforces project structure
/// rules from <c>dotnet-coding-standards.instructions.md</c>: file-scoped
/// namespaces, single type per file, file name matches type name, and no
/// <c>#pragma warning disable</c>.
/// </summary>
/// <remarks>
/// Request <c>data</c>:
/// <c>{ "content": "&lt;csharp-source&gt;", "filePath": "&lt;path&gt;",
/// "editorconfig.csharp_style_namespace_declarations": "file_scoped|block_scoped" }</c><br/>
/// <c>filePath</c> is the engine's name for the analysed file's path; the
/// legacy MCP server still sends it as <c>originalPath</c>, which is read when
/// <c>filePath</c> is absent until that server retires.<br/>
/// Response <c>output</c>: <c>{ "passed": &lt;bool&gt;, "report": "&lt;text&gt;" }</c>
/// </remarks>
internal sealed class AnalyzeCSharpProjectStructureTask : IMcpTask
{
    public string TaskName => "analyze_csharp_project_structure";

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

        var filePath = data.TryGetString("filePath") ?? data.TryGetString("originalPath") ?? string.Empty;
        var fileName = string.IsNullOrEmpty(filePath) ? string.Empty : Path.GetFileName(filePath);
        var namespacePreference = data.TryGetString("editorconfig.csharp_style_namespace_declarations");

        var (passed, report) = await BuildReportAsync(content, fileName, namespacePreference, cancellationToken).ConfigureAwait(false);

        var output = new JsonObject
        {
            ["passed"] = passed,
            ["report"] = report,
        };

        return JsonSerializer.SerializeToElement(output);
    }

    private static async Task<(bool Passed, string Report)> BuildReportAsync(
        string content,
        string fileName,
        string? namespacePreference,
        CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var violations = new List<string>();

        var effectiveNamespace = namespacePreference ?? "file_scoped";

        if (effectiveNamespace == "block_scoped")
        {
            AnalyzeBlockScopedNamespace(root, tree, violations);
        }
        else
        {
            AnalyzeFileScopedNamespace(root, tree, violations);
        }

        AnalyzeSingleTypePerFile(root, violations);
        AnalyzeFileNameMatchesType(root, fileName, violations);
        AnalyzePragmaWarningDisable(root, tree, violations);

        if (violations.Count == 0)
        {
            return (true, "✅ Project structure is correct.");
        }

        var report = $"❌ Found {violations.Count} project structure violation(s):\n" +
                     string.Join('\n', violations.Select((v, i) => $"  {i + 1}. {v}"));

        return (false, report);
    }

    // EditorConfig: csharp_style_namespace_declarations (block-scoped)
    private static void AnalyzeBlockScopedNamespace(SyntaxNode root, SyntaxTree tree, List<string> violations)
    {
        var fileScopedNamespaces = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().ToList();

        foreach (var ns in fileScopedNamespaces)
        {
            var line = tree.GetLineSpan(ns.Span).StartLinePosition.Line + 1;
            violations.Add(
                $"Line {line}: File-scoped namespace '{ns.Name}' detected. " +
                "Use a block-scoped namespace instead (csharp_style_namespace_declarations = block_scoped).");
        }
    }

    // EditorConfig: csharp_style_namespace_declarations (file-scoped)
    private static void AnalyzeFileScopedNamespace(SyntaxNode root, SyntaxTree tree, List<string> violations)
    {
        var blockNamespaces = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().ToList();

        foreach (var ns in blockNamespaces)
        {
            var line = tree.GetLineSpan(ns.Span).StartLinePosition.Line + 1;
            violations.Add(
                $"Line {line}: Block-scoped namespace '{ns.Name}' detected. " +
                "Use a file-scoped namespace instead (e.g., 'namespace MyApp.Services;').");
        }
    }

    // [coding-standards INST0016]: single type per file, file name matches type
    private static void AnalyzeSingleTypePerFile(SyntaxNode root, List<string> violations)
    {
        var topLevelTypes = CollectTopLevelTypes(root);

        if (topLevelTypes.Count <= 1)
        {
            return;
        }

        var names = topLevelTypes.Select(GetTypeName).ToList();

        violations.Add(
            $"File contains {topLevelTypes.Count} top-level type declarations ({string.Join(", ", names.Select(n => $"'{n}'"))}). " +
            "Keep a single type per file and name the file after that type.");
    }

    // [coding-standards INST0016]: file name matches type name
    private static void AnalyzeFileNameMatchesType(SyntaxNode root, ReadOnlySpan<char> fileName, List<string> violations)
    {
        if (fileName.IsEmpty || fileName.IsWhiteSpace())
        {
            return;
        }

        var topLevelTypes = CollectTopLevelTypes(root);

        // Only check file name when there is exactly one type; multi-type files
        // are already covered by AnalyzeSingleTypePerFile.
        if (topLevelTypes.Count != 1)
        {
            return;
        }

        var expectedName = Path.GetFileNameWithoutExtension(fileName);
        var actualName = GetTypeName(topLevelTypes[0]);

        if (!expectedName.Equals(actualName, StringComparison.Ordinal))
        {
            violations.Add(
                $"File name '{fileName}' does not match the type name '{actualName}'. " +
                $"Rename the file to '{actualName}.cs'.");
        }
    }

    /// <summary>
    /// Collects all top-level type declarations (classes, structs, interfaces, enums, records, delegates)
    /// from file-scoped namespaces, block-scoped namespaces, and the compilation-unit level.
    /// </summary>
    private static List<MemberDeclarationSyntax> CollectTopLevelTypes(SyntaxNode root)
    {
        var types = new List<MemberDeclarationSyntax>();

        foreach (var fileScopedNs in root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>())
        {
            types.AddRange(fileScopedNs.Members.OfType<BaseTypeDeclarationSyntax>());
            types.AddRange(fileScopedNs.Members.OfType<DelegateDeclarationSyntax>());
        }

        foreach (var blockNs in root.DescendantNodes().OfType<NamespaceDeclarationSyntax>())
        {
            types.AddRange(blockNs.Members.OfType<BaseTypeDeclarationSyntax>());
            types.AddRange(blockNs.Members.OfType<DelegateDeclarationSyntax>());
        }

        if (root is CompilationUnitSyntax compilationUnit)
        {
            types.AddRange(compilationUnit.Members.OfType<BaseTypeDeclarationSyntax>());
            types.AddRange(compilationUnit.Members.OfType<DelegateDeclarationSyntax>());
        }

        return types;
    }

    private static string GetTypeName(MemberDeclarationSyntax member)
        => member switch
        {
            BaseTypeDeclarationSyntax t => t.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            _ => string.Empty,
        };

    // [csharp INST0013]: no #pragma warning disable
    private static void AnalyzePragmaWarningDisable(SyntaxNode root, SyntaxTree tree, List<string> violations)
    {
        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.PragmaWarningDirectiveTrivia))
            {
                continue;
            }

            if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax directive)
            {
                continue;
            }

            if (!directive.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword))
            {
                continue;
            }

            var line = tree.GetLineSpan(trivia.Span).StartLinePosition.Line + 1;
            violations.Add(
                $"Line {line}: '#pragma warning disable' is not preferred. " +
                "Use [SuppressMessage] attribute with a justification instead.");
        }
    }
}
