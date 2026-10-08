namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AutoContext.Workers.Core;
using AutoContext.Workers.Core.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// <c>analyze_csharp_coding_style</c> — enforces code style rules from
/// <c>lang-csharp.instructions.md</c>: regions, decorative comments, curly
/// braces, blank lines before control flow, expression-body arrow placement,
/// XML doc comments on public/protected members, System-directive ordering,
/// and expression-body style for methods and properties.
/// </summary>
/// <remarks>
/// Request <c>data</c>:
/// <c>{ "content": "&lt;csharp-source&gt;",
/// "editorconfig.csharp_prefer_braces": "...",
/// "editorconfig.dotnet_sort_system_directives_first": "...",
/// "editorconfig.csharp_style_expression_bodied_methods": "...",
/// "editorconfig.csharp_style_expression_bodied_properties": "...",
/// "filePath": "&lt;abs-path&gt;" }</c><br/>
/// XML doc comments are required only on members visible outside the
/// assembly — public or protected, inside types that are themselves visible —
/// and only suggested in application projects, which publish no API.
/// <c>filePath</c> (legacy <c>originalPath</c>) locates the project.<br/>
/// Response <c>output</c>: <c>{ "passed", "report", "findings" }</c>
/// </remarks>
internal sealed partial class AnalyzeCSharpCodingStyleTask : IMcpTask
{
    private const string PassText = "Code style is correct.";
    private const string ViolationNoun = "style";

    public string TaskName => "analyze_csharp_coding_style";

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

        var bracePreference = data.TryGetString("editorconfig.csharp_prefer_braces") ?? "true";
        var sortSystemRaw = data.TryGetString("editorconfig.dotnet_sort_system_directives_first");
        var sortSystemFirst = sortSystemRaw is null
            || !string.Equals(sortSystemRaw, "false", StringComparison.OrdinalIgnoreCase);
        var expressionBodiedMethods = data.TryGetString("editorconfig.csharp_style_expression_bodied_methods");
        var expressionBodiedProperties = data.TryGetString("editorconfig.csharp_style_expression_bodied_properties");

        var projectKind = CSharpProjectKindResolver.Resolve(data.TryGetString("filePath") ?? data.TryGetString("originalPath"));

        var findings = await AnalyzeAsync(
            content,
            projectKind,
            bracePreference,
            sortSystemFirst,
            expressionBodiedMethods,
            expressionBodiedProperties,
            cancellationToken).ConfigureAwait(false);

        return findings.ToOutput(PassText, ViolationNoun);
    }

    private static async Task<AnalyzerFindings> AnalyzeAsync(
        string content,
        CSharpProjectKind projectKind,
        string bracePreference,
        bool sortSystemFirst,
        string? expressionBodiedMethods,
        string? expressionBodiedProperties,
        CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var normalized = content.ReplaceLineEndings("\n");
        ReadOnlySpan<char> contentSpan = normalized;
        var lineCount = contentSpan.Count('\n') + 1;
        var lineRanges = new Range[lineCount];
        contentSpan.Split(lineRanges, '\n');
        var findings = new AnalyzerFindings();

        AnalyzeRegions(root, tree, findings);
        AnalyzeDecorativeComments(contentSpan, lineRanges, findings);
        AnalyzeBlankLineBeforeControlFlow(root, tree, contentSpan, lineRanges, findings);
        AnalyzeExpressionBodyArrowPlacement(root, tree, findings);
        AnalyzeXmlDocComments(root, tree, projectKind, findings);
        AnalyzeCurlyBraces(root, tree, bracePreference, findings);

        if (sortSystemFirst)
        {
            AnalyzeSortSystemDirectivesFirst(root, tree, findings);
        }

        if (expressionBodiedMethods is not null)
        {
            AnalyzeExpressionBodiedMethods(root, tree, expressionBodiedMethods, findings);
        }

        if (expressionBodiedProperties is not null)
        {
            AnalyzeExpressionBodiedProperties(root, tree, expressionBodiedProperties, findings);
        }

        return findings;
    }

    // no #region directives
    private static void AnalyzeRegions(SyntaxNode root, SyntaxTree tree, AnalyzerFindings findings)
    {
        foreach (var trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia))
            {
                var line = tree.GetLineSpan(trivia.Span).StartLinePosition.Line + 1;
                findings.Add("lang-csharp#INST0005", line, $"#region directives are not allowed. They hide code structure.");
            }
        }
    }

    // no decorative section-header comments
    private static void AnalyzeDecorativeComments(
        ReadOnlySpan<char> content,
        ReadOnlySpan<Range> lineRanges,
        AnalyzerFindings findings)
    {
        for (var i = 0; i < lineRanges.Length; i++)
        {
            if (DecorativeCommentRegex().IsMatch(content[lineRanges[i]]))
            {
                findings.Add("lang-csharp#INST0022", i + 1,
                    $"Decorative section-header comment detected. " +
                    "Organize code through consistent member ordering instead.");
            }
        }
    }

    // curly braces for control flow statements
    private static void AnalyzeCurlyBraces(
        SyntaxNode root,
        SyntaxTree tree,
        string preference,
        AnalyzerFindings findings)
    {
        var controlFlowStatements = root.DescendantNodes()
            .Where(n => n is IfStatementSyntax or ElseClauseSyntax
                        or ForStatementSyntax or ForEachStatementSyntax
                        or WhileStatementSyntax or DoStatementSyntax
                        or UsingStatementSyntax or LockStatementSyntax
                        or FixedStatementSyntax);

        foreach (var node in controlFlowStatements)
        {
            var embedded = GetEmbeddedStatement(node);

            if (embedded is null)
            {
                continue;
            }

            var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
            var keyword = GetControlFlowKeyword(node);

            switch (preference)
            {
                case "false":
                    if (embedded is BlockSyntax block && block.Statements.Count == 1
                        && !IsMultilineStatement(block.Statements[0], tree))
                    {
                        findings.Add("editorconfig#csharp_prefer_braces", line,
                            $"'{keyword}' statement has unnecessary curly braces " +
                            "around a single-line body (csharp_prefer_braces = false).");
                    }

                    break;

                case "when_multiline":
                    if (embedded is BlockSyntax wmBlock && wmBlock.Statements.Count == 1
                        && !IsMultilineStatement(wmBlock.Statements[0], tree))
                    {
                        findings.Add("editorconfig#csharp_prefer_braces", line,
                            $"'{keyword}' statement has unnecessary curly braces " +
                            "around a single-line body (csharp_prefer_braces = when_multiline).");
                    }
                    else if (embedded is not BlockSyntax
                             && IsMultilineStatement(embedded, tree))
                    {
                        findings.Add("editorconfig#csharp_prefer_braces", line,
                            $"'{keyword}' statement requires curly braces " +
                            "around a multi-line body (csharp_prefer_braces = when_multiline).");
                    }

                    break;

                default: // "true" or unrecognized — require braces
                    if (embedded is not BlockSyntax && !IsGuardClause(node, embedded))
                    {
                        findings.Add("lang-csharp#INST0018", line,
                            $"'{keyword}' statement requires curly braces " +
                            "(exception: single-line guard clauses).");
                    }

                    break;
            }
        }
    }

    private static bool IsMultilineStatement(StatementSyntax statement, SyntaxTree tree)
    {
        var span = tree.GetLineSpan(statement.Span);

        return span.StartLinePosition.Line != span.EndLinePosition.Line;
    }

    // blank line before control flow statements
    private static void AnalyzeBlankLineBeforeControlFlow(
        SyntaxNode root,
        SyntaxTree tree,
        ReadOnlySpan<char> content,
        ReadOnlySpan<Range> lineRanges,
        AnalyzerFindings findings)
    {
        var controlFlowNodes = root.DescendantNodes()
            .Where(n => n is IfStatementSyntax or ForStatementSyntax
                        or ForEachStatementSyntax or WhileStatementSyntax
                        or SwitchStatementSyntax or DoStatementSyntax
                        or TryStatementSyntax or UsingStatementSyntax
                        or LockStatementSyntax);

        foreach (var node in controlFlowNodes)
        {
            // Skip nodes nested inside another control-flow (else-if chains, etc.)
            if (node.Parent is ElseClauseSyntax or IfStatementSyntax)
            {
                continue;
            }

            var lineIndex = tree.GetLineSpan(node.Span).StartLinePosition.Line;

            if (lineIndex < 1)
            {
                continue;
            }

            // Skip if this is the first statement in a block
            if (IsFirstStatementInBlock(node))
            {
                continue;
            }

            var previousLine = content[lineRanges[lineIndex - 1]].Trim();

            if (previousLine.Length > 0 && !(previousLine.Length == 1 && previousLine[0] == '{'))
            {
                findings.Suggest("lang-csharp#INST0015", lineIndex + 1,
                    $"Missing blank line before " +
                    $"'{GetControlFlowKeyword(node)}' statement.");
            }
        }
    }

    // expression-body arrow on the next line
    private static void AnalyzeExpressionBodyArrowPlacement(
        SyntaxNode root,
        SyntaxTree tree,
        AnalyzerFindings findings)
    {
        var arrowClauses = root.DescendantNodes().OfType<ArrowExpressionClauseSyntax>();

        foreach (var arrow in arrowClauses)
        {
            // Only check method/property/indexer/operator declarations — skip lambdas
            if (arrow.Parent is not (MethodDeclarationSyntax
                or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax
                or LocalFunctionStatementSyntax))
            {
                continue;
            }

            var arrowToken = arrow.ArrowToken;
            var arrowLine = tree.GetLineSpan(arrowToken.Span).StartLinePosition.Line;
            var parentStartLine = tree.GetLineSpan(arrow.Parent!.Span).StartLinePosition.Line;

            if (arrowLine == parentStartLine)
            {
                var line = arrowLine + 1;
                findings.Suggest("lang-csharp#INST0017", line,
                    $"Expression-body arrow (=>) must be on the next line, " +
                    "not at the end of the signature.");
            }
        }
    }

    private static StatementSyntax? GetEmbeddedStatement(SyntaxNode node) =>
        node switch
        {
            IfStatementSyntax ifs => ifs.Statement,
            ElseClauseSyntax els => els.Statement,
            ForStatementSyntax fors => fors.Statement,
            ForEachStatementSyntax foreachs => foreachs.Statement,
            WhileStatementSyntax whiles => whiles.Statement,
            DoStatementSyntax dos => dos.Statement,
            UsingStatementSyntax usings => usings.Statement,
            LockStatementSyntax locks => locks.Statement,
            FixedStatementSyntax fixeds => fixeds.Statement,
            _ => null,
        };

    private static bool IsGuardClause(SyntaxNode controlFlowNode, StatementSyntax embedded)
    {
        // A guard clause is a single-line if with return/throw/continue/break and no else
        if (controlFlowNode is not IfStatementSyntax ifStmt)
        {
            return false;
        }

        if (ifStmt.Else is not null)
        {
            return false;
        }

        return embedded is ReturnStatementSyntax
               or ThrowStatementSyntax
               or ContinueStatementSyntax
               or BreakStatementSyntax;
    }

    private static string GetControlFlowKeyword(SyntaxNode node) =>
        node switch
        {
            IfStatementSyntax => "if",
            ElseClauseSyntax => "else",
            ForStatementSyntax => "for",
            ForEachStatementSyntax => "foreach",
            WhileStatementSyntax => "while",
            DoStatementSyntax => "do",
            SwitchStatementSyntax => "switch",
            TryStatementSyntax => "try",
            UsingStatementSyntax => "using",
            LockStatementSyntax => "lock",
            FixedStatementSyntax => "fixed",
            _ => "control flow",
        };

    private static bool IsFirstStatementInBlock(SyntaxNode node)
    {
        if (node.Parent is not BlockSyntax block)
        {
            return false;
        }

        return block.Statements.FirstOrDefault() == node;
    }

    // XML doc comments on members visible outside the assembly
    private static void AnalyzeXmlDocComments(
        SyntaxNode root,
        SyntaxTree tree,
        CSharpProjectKind projectKind,
        AnalyzerFindings findings)
    {
        var severity = projectKind == CSharpProjectKind.Application
            ? AnalyzerFindingSeverity.Suggestion
            : AnalyzerFindingSeverity.Violation;

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            // A member of a type nobody outside the assembly can see is not public API.
            if (TestDetection.IsTestClass(typeDecl) || !IsVisibleOutsideAssembly(typeDecl))
            {
                continue;
            }

            AnalyzeMemberXmlDoc(typeDecl, tree, severity, findings);

            foreach (var member in typeDecl.Members)
            {
                if (!IsPublicOrProtected(member))
                {
                    continue;
                }

                if (member is MethodDeclarationSyntax method
                    && method.Modifiers.Any(SyntaxKind.OverrideKeyword))
                {
                    continue;
                }

                // Skip nested types — they are checked as top-level type declarations
                if (member is TypeDeclarationSyntax)
                {
                    continue;
                }

                AnalyzeMemberXmlDoc(member, tree, severity, findings);
            }
        }

        // Also check top-level enums and delegates
        foreach (var enumDecl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            if (IsVisibleOutsideAssembly(enumDecl))
            {
                AnalyzeMemberXmlDoc(enumDecl, tree, severity, findings);
            }
        }

        foreach (var delegateDecl in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (IsVisibleOutsideAssembly(delegateDecl))
            {
                AnalyzeMemberXmlDoc(delegateDecl, tree, severity, findings);
            }
        }
    }

    private static void AnalyzeMemberXmlDoc(
        MemberDeclarationSyntax member,
        SyntaxTree tree,
        AnalyzerFindingSeverity severity,
        AnalyzerFindings findings)
    {
        var hasXmlDoc = member.GetLeadingTrivia()
            .Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));

        if (hasXmlDoc)
        {
            return;
        }

        var line = tree.GetLineSpan(member.Span).StartLinePosition.Line + 1;
        var name = GetMemberDisplayName(member);
        findings.Add("lang-csharp#INST0021", severity, line, $"Public/protected member '{name}' is missing XML doc comment (/// <summary>).");
    }

    private static bool IsVisibleOutsideAssembly(MemberDeclarationSyntax member)
        => IsPublicOrProtected(member)
           && member.Ancestors().OfType<TypeDeclarationSyntax>().All(IsPublicOrProtected);

    private static bool IsPublicOrProtected(MemberDeclarationSyntax member)
    {
        var modifiers = member.Modifiers;

        return modifiers.Any(SyntaxKind.PublicKeyword)
               || modifiers.Any(SyntaxKind.ProtectedKeyword);
    }

    private static string GetMemberDisplayName(MemberDeclarationSyntax member)
        => member switch
        {
            TypeDeclarationSyntax t => t.Identifier.Text,
            EnumDeclarationSyntax e => e.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            EventDeclarationSyntax ev => ev.Identifier.Text,
            EventFieldDeclarationSyntax ef
                => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "(event)",
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.Text}",
            ConversionOperatorDeclarationSyntax co => $"operator {co.Type}",
            FieldDeclarationSyntax f
                => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "(field)",
            _ => "(unknown)",
        };

    // EditorConfig: dotnet_sort_system_directives_first
    private static void AnalyzeSortSystemDirectivesFirst(
        SyntaxNode root,
        SyntaxTree tree,
        AnalyzerFindings findings)
    {
        if (root is CompilationUnitSyntax compilationUnit)
        {
            AnalyzeUsingsOrder(compilationUnit.Usings, tree, findings);
        }

        foreach (var namespaceDecl in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            AnalyzeUsingsOrder(namespaceDecl.Usings, tree, findings);
        }
    }

    private static void AnalyzeUsingsOrder(
        SyntaxList<UsingDirectiveSyntax> usings,
        SyntaxTree tree,
        AnalyzerFindings findings)
    {
        var seenNonSystem = false;

        foreach (var usingDirective in usings)
        {
            if (usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)
                || usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                || usingDirective.Alias is not null)
            {
                continue;
            }

            var name = usingDirective.Name?.ToString() ?? string.Empty;
            var isSystem = name == "System" || name.StartsWith("System.", StringComparison.Ordinal);

            if (!isSystem)
            {
                seenNonSystem = true;
            }
            else if (seenNonSystem)
            {
                var line = tree.GetLineSpan(usingDirective.Span).StartLinePosition.Line + 1;
                findings.Add("editorconfig#dotnet_sort_system_directives_first", line,
                    $"'using {name}' must come before non-System using directives " +
                    "(dotnet_sort_system_directives_first = true).");
            }
        }
    }

    // EditorConfig: csharp_style_expression_bodied_methods
    private static void AnalyzeExpressionBodiedMethods(
        SyntaxNode root,
        SyntaxTree tree,
        string preference,
        AnalyzerFindings findings)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            switch (preference)
            {
                case "false":
                case "never":
                    if (method.ExpressionBody is not null)
                    {
                        var line = tree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
                        findings.Add("editorconfig#csharp_style_expression_bodied_methods", line,
                            $"Method '{method.Identifier.Text}' uses an expression body; " +
                            "prefer a block body (csharp_style_expression_bodied_methods = never).");
                    }

                    break;

                case "true":
                case "always":
                    if (method.Body is { } body
                        && method.ExpressionBody is null
                        && body.Statements.Count == 1
                        && body.Statements[0] is ReturnStatementSyntax { Expression: not null })
                    {
                        var line = tree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
                        findings.Add("editorconfig#csharp_style_expression_bodied_methods", line,
                            $"Method '{method.Identifier.Text}' has a single return statement; " +
                            "prefer expression-body syntax (csharp_style_expression_bodied_methods = always).");
                    }

                    break;

                case "when_on_single_line":
                    if (method.Body is { } whenBody
                        && method.ExpressionBody is null
                        && whenBody.Statements.Count == 1
                        && whenBody.Statements[0] is ReturnStatementSyntax { Expression: not null } retStmt
                        && IsExpressionOnSingleLine(retStmt.Expression, tree))
                    {
                        var line = tree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
                        findings.Add("editorconfig#csharp_style_expression_bodied_methods", line,
                            $"Method '{method.Identifier.Text}' has a single-line return; " +
                            "prefer expression-body syntax (csharp_style_expression_bodied_methods = when_on_single_line).");
                    }

                    break;

                default:
                    break;
            }
        }
    }

    // EditorConfig: csharp_style_expression_bodied_properties
    private static void AnalyzeExpressionBodiedProperties(
        SyntaxNode root,
        SyntaxTree tree,
        string preference,
        AnalyzerFindings findings)
    {
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            switch (preference)
            {
                case "false":
                case "never":
                    if (property.ExpressionBody is not null)
                    {
                        var line = tree.GetLineSpan(property.Span).StartLinePosition.Line + 1;
                        findings.Add("editorconfig#csharp_style_expression_bodied_properties", line,
                            $"Property '{property.Identifier.Text}' uses an expression body; " +
                            "prefer a block body with a getter accessor (csharp_style_expression_bodied_properties = never).");
                    }

                    break;

                case "true":
                case "always":
                    if (IsGetOnlySingleReturnProperty(property, out _))
                    {
                        var line = tree.GetLineSpan(property.Span).StartLinePosition.Line + 1;
                        findings.Add("editorconfig#csharp_style_expression_bodied_properties", line,
                            $"Property '{property.Identifier.Text}' has a get accessor with a single return; " +
                            "prefer expression-body syntax (csharp_style_expression_bodied_properties = always).");
                    }

                    break;

                case "when_on_single_line":
                    if (IsGetOnlySingleReturnProperty(property, out var returnExpr)
                        && returnExpr is not null
                        && IsExpressionOnSingleLine(returnExpr, tree))
                    {
                        var line = tree.GetLineSpan(property.Span).StartLinePosition.Line + 1;
                        findings.Add("editorconfig#csharp_style_expression_bodied_properties", line,
                            $"Property '{property.Identifier.Text}' has a single-line get return; " +
                            "prefer expression-body syntax (csharp_style_expression_bodied_properties = when_on_single_line).");
                    }

                    break;

                default:
                    break;
            }
        }
    }

    private static bool IsGetOnlySingleReturnProperty(
        PropertyDeclarationSyntax property,
        out ExpressionSyntax? returnExpression)
    {
        returnExpression = null;

        if (property.ExpressionBody is not null)
        {
            return false;
        }

        var accessorList = property.AccessorList;

        if (accessorList is null)
        {
            return false;
        }

        var accessors = accessorList.Accessors;

        if (accessors.Count != 1 || !accessors[0].IsKind(SyntaxKind.GetAccessorDeclaration))
        {
            return false;
        }

        var getter = accessors[0];

        if (getter.Body is null || getter.ExpressionBody is not null)
        {
            return false;
        }

        if (getter.Body.Statements.Count != 1
            || getter.Body.Statements[0] is not ReturnStatementSyntax { Expression: not null } returnStmt)
        {
            return false;
        }

        returnExpression = returnStmt.Expression;

        return true;
    }

    private static bool IsExpressionOnSingleLine(SyntaxNode node, SyntaxTree tree)
    {
        var span = tree.GetLineSpan(node.Span);

        return span.StartLinePosition.Line == span.EndLinePosition.Line;
    }

    [GeneratedRegex(
        @"^\s*//\s*[─═━—–\-_]{2,}\s*\S+.*[─═━—–\-_]{2,}|^\s*//\s*[─═━—–\-_]{3,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex DecorativeCommentRegex();
}
