namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Workers.Core;
using AutoContext.Workers.Core.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// <c>analyze_csharp_naming_conventions</c> — enforces naming conventions from
/// <c>lang-csharp.instructions.md</c> and <c>dotnet-coding-standards.instructions.md</c>:
/// interface <c>I</c> prefix, extension class <c>Extensions</c> suffix, async
/// method <c>Async</c> suffix, private instance field <c>_camelCase</c>,
/// PascalCase for types/methods/properties/events, and camelCase for parameters.
/// </summary>
/// <remarks>
/// Request <c>data</c>:  <c>{ "content": "&lt;csharp-source&gt;" }</c><br/>
/// Response <c>output</c>: <c>{ "passed": &lt;bool&gt;, "report": "&lt;text&gt;" }</c>
/// </remarks>
internal sealed class AnalyzeCSharpNamingConventionsTask : IMcpTask
{
    private const string PassText = "Naming conventions are correct.";
    private const string ViolationNoun = "naming";

    public string TaskName => "analyze_csharp_naming_conventions";

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

        var findings = await AnalyzeAsync(content, cancellationToken).ConfigureAwait(false);

        return findings.ToOutput(PassText, ViolationNoun);
    }

    private static async Task<AnalyzerFindings> AnalyzeAsync(string content, CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var findings = new AnalyzerFindings();

        AnalyzeInterfaceNames(root, tree, findings);
        AnalyzeExtensionClassNames(root, tree, findings);
        AnalyzeAsyncMethodNames(root, tree, findings);
        AnalyzePrivateFieldNames(root, tree, findings);
        AnalyzePascalCaseTypes(root, tree, findings);
        AnalyzePascalCaseMethods(root, tree, findings);
        AnalyzePascalCaseProperties(root, tree, findings);
        AnalyzePascalCaseEvents(root, tree, findings);
        AnalyzeCamelCaseParameters(root, tree, findings);

        return findings;
    }

    // interface I prefix
    private static void AnalyzeInterfaceNames(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var iface in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
        {
            var name = iface.Identifier.Text;

            if (!IsValidInterfaceName(name))
            {
                var line = tree.GetLineSpan(iface.Span).StartLinePosition.Line + 1;
                findings.Add("dotnet-coding-standards#INST0011", line,
                    $"Interface '{name}' must be prefixed with 'I' followed by an uppercase letter " +
                    $"(e.g., 'I{name}').");
            }
        }
    }

    private static bool IsValidInterfaceName(string name)
        => name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);

    // extension class Extensions suffix
    private static void AnalyzeExtensionClassNames(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!classDecl.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                continue;
            }

            var hasExtensionMethod = classDecl.Members
                .OfType<MethodDeclarationSyntax>()
                .Any(IsExtensionMethod);

            if (!hasExtensionMethod)
            {
                continue;
            }

            var name = classDecl.Identifier.Text;

            if (!name.EndsWith("Extensions", StringComparison.Ordinal))
            {
                var line = tree.GetLineSpan(classDecl.Span).StartLinePosition.Line + 1;
                findings.Add("dotnet-coding-standards#INST0013", line,
                    $"Extension class '{name}' must be suffixed with 'Extensions' " +
                    $"(e.g., '{name}Extensions').");
            }
        }
    }

    private static bool IsExtensionMethod(MethodDeclarationSyntax method)
        => method.Modifiers.Any(SyntaxKind.StaticKeyword)
           && method.ParameterList.Parameters.Count > 0
           && method.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword);

    // async method Async suffix
    private static void AnalyzeAsyncMethodNames(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                continue;
            }

            if (method.Modifiers.Any(SyntaxKind.OverrideKeyword))
            {
                continue;
            }

            if (TestDetection.HasTestAttribute(method))
            {
                continue;
            }

            if (TestDetection.IsLikelyEventHandler(method))
            {
                continue;
            }

            var name = method.Identifier.Text;

            if (!name.EndsWith("Async", StringComparison.Ordinal))
            {
                var line = tree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
                findings.Add("dotnet-coding-standards#INST0014", line,
                    $"Async method '{name}' must be suffixed with 'Async' " +
                    $"(e.g., '{name}Async').");
            }
        }
    }

    // private instance fields _camelCase
    private static void AnalyzePrivateFieldNames(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            // Constants use PascalCase and static fields may follow different conventions — skip both.
            if (field.Modifiers.Any(SyntaxKind.ConstKeyword)
                || field.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                continue;
            }

            if (!IsPrivateOrDefault(field.Modifiers))
            {
                continue;
            }

            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.Text;

                if (!IsValidPrivateFieldName(name))
                {
                    var line = tree.GetLineSpan(variable.Span).StartLinePosition.Line + 1;
                    findings.Add("lang-csharp#INST0001", line,
                        $"Private field '{name}' must use _camelCase naming " +
                        "(start with an underscore followed by a lowercase letter, e.g., '_myField').");
                }
            }
        }
    }

    private static bool IsValidPrivateFieldName(string name)
        => name.Length >= 2 && name[0] == '_' && char.IsLower(name[1]);

    private static bool IsPrivateOrDefault(SyntaxTokenList modifiers)
    {
        var hasPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        var hasInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        var hasProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);

        return !hasPublic && !hasInternal && !hasProtected;
    }

    // .NET naming conventions (PascalCase types)
    private static void AnalyzePascalCaseTypes(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var name = typeDecl.Identifier.Text;

            if (!IsPascalCase(name))
            {
                var line = tree.GetLineSpan(typeDecl.Span).StartLinePosition.Line + 1;
                findings.Add("dotnet-coding-standards#INST0001", line,
                    $"Type '{name}' must use PascalCase naming (start with an uppercase letter).");
            }
        }

        foreach (var delegateDecl in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            var name = delegateDecl.Identifier.Text;

            if (!IsPascalCase(name))
            {
                var line = tree.GetLineSpan(delegateDecl.Span).StartLinePosition.Line + 1;
                findings.Add("dotnet-coding-standards#INST0001", line,
                    $"Delegate '{name}' must use PascalCase naming (start with an uppercase letter).");
            }
        }
    }

    // .NET naming conventions (PascalCase methods)
    private static void AnalyzePascalCaseMethods(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var isTestClass = TestDetection.IsTestClass(typeDecl);

            foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                // Test method names follow Should_do_something convention — skip PascalCase check.
                if (isTestClass && TestDetection.HasTestAttribute(method))
                {
                    continue;
                }

                var name = method.Identifier.Text;

                if (!IsPascalCase(name))
                {
                    var line = tree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
                    findings.Add("dotnet-coding-standards#INST0001", line,
                        $"Method '{name}' must use PascalCase naming (start with an uppercase letter).");
                }
            }
        }
    }

    // .NET naming conventions (PascalCase properties)
    private static void AnalyzePascalCaseProperties(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var name = property.Identifier.Text;

            if (!IsPascalCase(name))
            {
                var line = tree.GetLineSpan(property.Span).StartLinePosition.Line + 1;
                findings.Add("dotnet-coding-standards#INST0001", line,
                    $"Property '{name}' must use PascalCase naming (start with an uppercase letter).");
            }
        }
    }

    // .NET naming conventions (PascalCase events)
    private static void AnalyzePascalCaseEvents(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var eventDecl in root.DescendantNodes().OfType<EventDeclarationSyntax>())
        {
            var name = eventDecl.Identifier.Text;

            if (!IsPascalCase(name))
            {
                var line = tree.GetLineSpan(eventDecl.Span).StartLinePosition.Line + 1;
                findings.Add("dotnet-coding-standards#INST0001", line,
                    $"Event '{name}' must use PascalCase naming (start with an uppercase letter).");
            }
        }

        foreach (var eventField in root.DescendantNodes().OfType<EventFieldDeclarationSyntax>())
        {
            foreach (var variable in eventField.Declaration.Variables)
            {
                var name = variable.Identifier.Text;

                if (!IsPascalCase(name))
                {
                    var line = tree.GetLineSpan(variable.Span).StartLinePosition.Line + 1;
                    findings.Add("dotnet-coding-standards#INST0001", line,
                        $"Event '{name}' must use PascalCase naming (start with an uppercase letter).");
                }
            }
        }
    }

    // .NET naming conventions (camelCase parameters)
    private static void AnalyzeCamelCaseParameters(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            // Only check method, constructor, delegate, and indexer parameters — not lambda params.
            if (parameter.Parent?.Parent is not (MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or DelegateDeclarationSyntax
                or IndexerDeclarationSyntax
                or LocalFunctionStatementSyntax))
            {
                continue;
            }

            // 'this' parameters in extension methods follow their own conventions.
            if (parameter.Modifiers.Any(SyntaxKind.ThisKeyword))
            {
                continue;
            }

            var name = parameter.Identifier.Text;

            // Skip discards.
            if (string.IsNullOrEmpty(name) || name == "_")
            {
                continue;
            }

            if (!IsCamelCase(name))
            {
                var line = tree.GetLineSpan(parameter.Span).StartLinePosition.Line + 1;
                findings.Add("dotnet-coding-standards#INST0001", line,
                    $"Parameter '{name}' must use camelCase naming " +
                    "(start with a lowercase letter, no leading underscore).");
            }
        }
    }

    private static bool IsPascalCase(string name)
        => name.Length > 0 && char.IsUpper(name[0]);

    private static bool IsCamelCase(string name)
        => name.Length > 0 && char.IsLower(name[0]);
}
