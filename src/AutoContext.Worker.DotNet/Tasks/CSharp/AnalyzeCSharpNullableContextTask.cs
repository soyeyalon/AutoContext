namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Workers.Core;
using AutoContext.Workers.Core.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// <c>analyze_csharp_nullable_context</c> — enforces nullable safety rules
/// from <c>lang-csharp.instructions.md</c>: no <c>#nullable disable</c>
/// directives and no use of the null-forgiving (<c>!</c>) operator to
/// suppress warnings.
/// </summary>
/// <remarks>
/// Request <c>data</c>:  <c>{ "content": "&lt;csharp-source&gt;" }</c><br/>
/// Response <c>output</c>: <c>{ "passed": &lt;bool&gt;, "report": "&lt;text&gt;" }</c>
/// </remarks>
internal sealed class AnalyzeCSharpNullableContextTask : IMcpTask
{
    private const string PassText = "Nullable context is correct.";
    private const string ViolationNoun = "nullable context";

    public string TaskName => "analyze_csharp_nullable_context";

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

        AnalyzeNullableDisable(root, tree, findings);
        AnalyzeNullForgivingOperator(root, tree, findings);

        return findings;
    }

    // keep #nullable enable
    private static void AnalyzeNullableDisable(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.NullableDirectiveTrivia))
            {
                continue;
            }

            if (trivia.GetStructure() is not NullableDirectiveTriviaSyntax directive)
            {
                continue;
            }

            if (directive.SettingToken.IsKind(SyntaxKind.DisableKeyword))
            {
                var line = tree.GetLineSpan(trivia.Span).StartLinePosition.Line + 1;
                findings.Add("lang-csharp#INST0019", line,
                    $"'#nullable disable' is not allowed. " +
                    "Keep nullable reference types enabled globally via <Nullable>enable</Nullable> in the project file.");
            }
        }
    }

    // no null-forgiving operator (!)
    private static void AnalyzeNullForgivingOperator(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var expression in root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>())
        {
            if (!expression.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                continue;
            }

            var line = tree.GetLineSpan(expression.Span).StartLinePosition.Line + 1;
            findings.Add("lang-csharp#INST0020", line,
                $"The null-forgiving operator '!' is not allowed. " +
                "Fix the underlying nullability issue instead of suppressing the warning.");
        }
    }
}
