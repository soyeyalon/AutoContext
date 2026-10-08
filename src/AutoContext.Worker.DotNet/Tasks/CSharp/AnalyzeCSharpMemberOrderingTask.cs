namespace AutoContext.Worker.DotNet.Tasks.CSharp;

using System.Text.Json;
using System.Text.Json.Nodes;

using AutoContext.Workers.Core;
using AutoContext.Workers.Core.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// <c>analyze_csharp_member_ordering</c> — enforces member ordering rules
/// from <c>lang-csharp.instructions.md</c>: members are ordered by kind,
/// access level, and static-before-instance.
/// </summary>
/// <remarks>
/// Request <c>data</c>:  <c>{ "content": "&lt;csharp-source&gt;" }</c><br/>
/// Response <c>output</c>: <c>{ "passed": &lt;bool&gt;, "report": "&lt;text&gt;" }</c>
/// </remarks>
internal sealed class AnalyzeCSharpMemberOrderingTask : IMcpTask
{
    private const string PassText = "Member ordering is correct.";
    private const string ViolationNoun = "ordering";

    public string TaskName => "analyze_csharp_member_ordering";

    private enum MemberKind
    {
        Constant = 0,
        StaticField = 1,
        Field = 2,
        Constructor = 3,
        Delegate = 4,
        Event = 5,
        Enum = 6,
        Property = 7,
        Indexer = 8,
        Method = 9,
        Operator = 10,
        NestedType = 11,
    }

    private enum AccessLevel
    {
        Public = 0,
        Internal = 1,
        ProtectedInternal = 2,
        Protected = 3,
        PrivateProtected = 4,
        Private = 5,
    }

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

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            AnalyzeType(typeDecl, tree, findings);
        }

        return findings;
    }

    // member ordering by kind, access level, static, alphabetical
    private static void AnalyzeType(TypeDeclarationSyntax typeDecl, SyntaxTree tree, AnalyzerFindings findings)
    {
        if (TestDetection.IsTestClass(typeDecl))
        {
            return;
        }

        var typeName = typeDecl.Identifier.Text;
        var members = typeDecl.Members;

        MemberKind? previousKind = null;
        AccessLevel? previousAccess = null;
        bool? previousIsStatic = null;
        string? previousName = null;

        foreach (var member in members)
        {
            var kind = GetMemberKind(member);
            if (kind is null)
            {
                continue;
            }

            var access = GetAccessLevel(member);
            var isStatic = IsStatic(member);
            var name = GetMemberName(member);
            var line = tree.GetLineSpan(member.Span).StartLinePosition.Line + 1;

            if (previousKind is not null)
            {
                if (kind < previousKind)
                {
                    findings.Add("lang-csharp#INST0004", line,
                        $"{typeName}: {KindLabel(kind.Value)} '{name}' " +
                        $"should appear before {KindLabel(previousKind.Value)} '{previousName}'.");
                }
                else if (kind == previousKind)
                {
                    if (access < previousAccess)
                    {
                        findings.Add("lang-csharp#INST0004", line,
                            $"{typeName}: {AccessLabel(access)} member '{name}' " +
                            $"should appear before {AccessLabel(previousAccess!.Value)} member '{previousName}'.");
                    }
                    else if (access == previousAccess)
                    {
                        if (isStatic && previousIsStatic == false)
                        {
                            findings.Add("lang-csharp#INST0004", line,
                                $"{typeName}: static member '{name}' " +
                                $"should appear before instance member '{previousName}'.");
                        }
                        else if (isStatic == previousIsStatic
                                 && string.Compare(name, previousName, StringComparison.Ordinal) < 0)
                        {
                            findings.Suggest("lang-csharp#INST0004", line,
                                $"{typeName}: member '{name}' " +
                                $"should appear before '{previousName}' (alphabetical order).");
                        }
                    }
                }
            }

            previousKind = kind;
            previousAccess = access;
            previousIsStatic = isStatic;
            previousName = name;
        }
    }

    private static MemberKind? GetMemberKind(MemberDeclarationSyntax member) =>
        member switch
        {
            FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.ConstKeyword) => MemberKind.Constant,
            FieldDeclarationSyntax when member.Modifiers.Any(SyntaxKind.StaticKeyword) => MemberKind.StaticField,
            FieldDeclarationSyntax => MemberKind.Field,
            ConstructorDeclarationSyntax => MemberKind.Constructor,
            DestructorDeclarationSyntax => MemberKind.Constructor,
            DelegateDeclarationSyntax => MemberKind.Delegate,
            EventDeclarationSyntax => MemberKind.Event,
            EventFieldDeclarationSyntax => MemberKind.Event,
            EnumDeclarationSyntax => MemberKind.Enum,
            PropertyDeclarationSyntax => MemberKind.Property,
            IndexerDeclarationSyntax => MemberKind.Indexer,
            MethodDeclarationSyntax => MemberKind.Method,
            OperatorDeclarationSyntax => MemberKind.Operator,
            ConversionOperatorDeclarationSyntax => MemberKind.Operator,
            TypeDeclarationSyntax => MemberKind.NestedType,
            _ => null,
        };

    private static AccessLevel GetAccessLevel(MemberDeclarationSyntax member)
    {
        var modifiers = member.Modifiers;

        var hasPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        var hasInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        var hasProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        var hasPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);

        return (hasPublic, hasInternal, hasProtected, hasPrivate) switch
        {
            (true, _, _, _) => AccessLevel.Public,
            (_, true, true, _) => AccessLevel.ProtectedInternal,
            (_, true, _, _) => AccessLevel.Internal,
            (_, _, true, true) => AccessLevel.PrivateProtected,
            (_, _, true, _) => AccessLevel.Protected,
            (_, _, _, true) => AccessLevel.Private,
            _ => AccessLevel.Private, // C# default
        };
    }

    private static bool IsStatic(MemberDeclarationSyntax member) =>
        member.Modifiers.Any(SyntaxKind.StaticKeyword);

    private static string GetMemberName(MemberDeclarationSyntax member) =>
        member switch
        {
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "(field)",
            PropertyDeclarationSyntax p => p.Identifier.Text,
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            DestructorDeclarationSyntax d => $"~{d.Identifier.Text}",
            TypeDeclarationSyntax t => t.Identifier.Text,
            EnumDeclarationSyntax e => e.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            EventDeclarationSyntax ev => ev.Identifier.Text,
            EventFieldDeclarationSyntax ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "(event)",
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.Text}",
            ConversionOperatorDeclarationSyntax co => $"operator {co.Type}",
            _ => "(unknown)",
        };

    private static string KindLabel(MemberKind kind) =>
        kind switch
        {
            MemberKind.Constant => "constant",
            MemberKind.StaticField => "static field",
            MemberKind.Field => "field",
            MemberKind.Constructor => "constructor",
            MemberKind.Delegate => "delegate",
            MemberKind.Event => "event",
            MemberKind.Enum => "enum",
            MemberKind.Property => "property",
            MemberKind.Indexer => "indexer",
            MemberKind.Method => "method",
            MemberKind.Operator => "operator",
            MemberKind.NestedType => "nested type",
            _ => kind.ToString(),
        };

    private static string AccessLabel(AccessLevel access) =>
        access switch
        {
            AccessLevel.Public => "public",
            AccessLevel.Internal => "internal",
            AccessLevel.ProtectedInternal => "protected internal",
            AccessLevel.Protected => "protected",
            AccessLevel.PrivateProtected => "private protected",
            AccessLevel.Private => "private",
            _ => access.ToString(),
        };
}
