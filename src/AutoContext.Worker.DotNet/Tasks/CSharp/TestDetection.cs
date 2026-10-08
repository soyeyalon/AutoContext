namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Shared helpers for detecting test classes and test attributes across C#
/// checker tasks.
/// </summary>
internal static class TestDetection
{
    internal static readonly HashSet<string> TestAttributes =
    [
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestCase", "TestCaseAttribute",
    ];

    /// <summary>
    /// Determines whether the given type declaration is a test class by checking
    /// if any of its methods have a known test attribute.
    /// </summary>
    internal static bool IsTestClass(TypeDeclarationSyntax typeDecl)
        => typeDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(HasTestAttribute);

    /// <summary>
    /// Determines whether <paramref name="node"/> sits inside a test class,
    /// directly or in a type nested within one.
    /// </summary>
    internal static bool IsInTestClass(SyntaxNode node)
        => node.Ancestors().OfType<TypeDeclarationSyntax>().Any(IsTestClass);

    /// <summary>
    /// Determines whether the given method has a known test attribute.
    /// </summary>
    internal static bool HasTestAttribute(MethodDeclarationSyntax method)
        => method.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => TestAttributes.Contains(GetAttributeName(a)));

    /// <summary>
    /// Extracts the simple name from an attribute syntax node.
    /// </summary>
    internal static string GetAttributeName(AttributeSyntax attr)
        => attr.Name switch
        {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => string.Empty,
        };

    /// <summary>
    /// Determines whether the given method is likely an event handler, using two
    /// heuristics: the conventional two-parameter signature where the second
    /// parameter type contains <c>EventArgs</c>, or the <c>OnXxx</c> naming
    /// convention used by WinForms, WPF, Blazor, MAUI, and ASP.NET.
    /// </summary>
    internal static bool IsLikelyEventHandler(MethodDeclarationSyntax method)
    {
        if (method.ReturnType is not PredefinedTypeSyntax returnType
            || !returnType.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return false;
        }

        var parameters = method.ParameterList.Parameters;

        // Conventional event handler: (object sender, XxxEventArgs e)
        if (parameters.Count == 2)
        {
            var lastParamType = parameters[1].Type?.ToString() ?? string.Empty;

            if (lastParamType.Contains("EventArgs", StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Naming convention: OnXxx methods are by convention event-handler overrides
        // or lifecycle callbacks in WinForms, WPF, Blazor, MAUI, and ASP.NET.
        var name = method.Identifier.Text;

        return name.Length > 2
               && name.StartsWith("On", StringComparison.Ordinal)
               && char.IsUpper(name[2]);
    }
}
