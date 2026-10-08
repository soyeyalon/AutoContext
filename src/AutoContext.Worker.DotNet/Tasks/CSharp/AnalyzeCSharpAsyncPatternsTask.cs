namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Workers.Core;
using AutoContext.Workers.Core.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// <c>analyze_csharp_async_patterns</c> — enforces async/await rules from
/// <c>dotnet-async-await.instructions.md</c>: no <c>async void</c> (except
/// event handlers), public async APIs require a <c>CancellationToken</c>
/// parameter, and <c>await</c> expressions in non-test code must use
/// <c>.ConfigureAwait(false)</c>.
/// </summary>
/// <remarks>
/// Request <c>data</c>:  <c>{ "content": "&lt;csharp-source&gt;" }</c><br/>
/// Response <c>output</c>: <c>{ "passed": &lt;bool&gt;, "report": "&lt;text&gt;" }</c>
/// </remarks>
internal sealed class AnalyzeCSharpAsyncPatternsTask : IMcpTask
{
    private const string PassText = "Async patterns are correct.";
    private const string ViolationNoun = "async pattern";

    public string TaskName => "analyze_csharp_async_patterns";

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

        AnalyzeAsyncVoid(root, tree, findings);
        AnalyzeCancellationToken(root, tree, findings);
        AnalyzeConfigureAwait(root, tree, findings);

        return findings;
    }

    // no async void except event handlers
    private static void AnalyzeAsyncVoid(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                continue;
            }

            if (method.ReturnType is not PredefinedTypeSyntax returnType
                || !returnType.Keyword.IsKind(SyntaxKind.VoidKeyword))
            {
                continue;
            }

            if (TestDetection.IsLikelyEventHandler(method))
            {
                continue;
            }

            var line = tree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
            findings.Add("dotnet-async-await#INST0007", line,
                $"Method '{method.Identifier.Text}' uses 'async void', which is not allowed. " +
                "Use 'async Task' instead — 'async void' swallows unhandled exceptions.");
        }
    }

    // public async APIs must have CancellationToken
    private static void AnalyzeCancellationToken(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword)
                || !method.Modifiers.Any(SyntaxKind.PublicKeyword))
            {
                continue;
            }

            if (method.Modifiers.Any(SyntaxKind.OverrideKeyword))
            {
                continue;
            }

            // Event handlers (async void) do not take a CancellationToken.
            if (method.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
            {
                continue;
            }

            var containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

            if (containingType is not null && TestDetection.IsTestClass(containingType))
            {
                continue;
            }

            if (HasCancellationTokenParameter(method))
            {
                continue;
            }

            var line = tree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
            findings.Add("dotnet-async-await#INST0002", line,
                $"Public async method '{method.Identifier.Text}' is missing a CancellationToken parameter. " +
                "Add 'CancellationToken cancellationToken = default' as the last parameter.");
        }
    }

    private static bool HasCancellationTokenParameter(MethodDeclarationSyntax method)
        => method.ParameterList.Parameters
            .Any(p => p.Type is not null && GetSimpleTypeName(p.Type) == "CancellationToken");

    private static string GetSimpleTypeName(TypeSyntax type)
        => type switch
        {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            NullableTypeSyntax nullable => GetSimpleTypeName(nullable.ElementType),
            _ => type.ToString(),
        };

    // .ConfigureAwait(false) in non-test code
    private static void AnalyzeConfigureAwait(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var awaitExpr in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            var containingType = awaitExpr.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

            if (containingType is not null && TestDetection.IsTestClass(containingType))
            {
                continue;
            }

            if (HasConfigureAwaitFalse(awaitExpr.Expression))
            {
                continue;
            }

            var line = tree.GetLineSpan(awaitExpr.Span).StartLinePosition.Line + 1;
            findings.Add("dotnet-async-await#INST0006", line,
                $"Awaited expression is missing '.ConfigureAwait(false)'. " +
                "Use 'await someTask.ConfigureAwait(false)' in non-test code.");
        }
    }

    private static bool HasConfigureAwaitFalse(ExpressionSyntax expression)
    {
        if (expression is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        if (memberAccess.Name.Identifier.Text != "ConfigureAwait")
        {
            return false;
        }

        var args = invocation.ArgumentList.Arguments;

        if (args.Count != 1)
        {
            return false;
        }

        return args[0].Expression.IsKind(SyntaxKind.FalseLiteralExpression);
    }
}
