namespace AutoContext.Instructions.Manifest.Generator;

using System.Text.RegularExpressions;

using AutoContext.Instructions.Parser.Model;
using AutoContext.Instructions.Parser.Syntax;

/// <inheritdoc cref="IInstructionsToolObligationValidator" />
internal sealed partial class InstructionsToolObligationValidator : IInstructionsToolObligationValidator
{
    /// <summary>
    /// The <c>##</c> headings whose inline-code tokens are under contract. Any other
    /// section may mention tools in prose (the always-attached files name the LM
    /// discovery tools, which live in code rather than the registry) and is not
    /// scanned.
    /// </summary>
    private static readonly HashSet<string> ContractedHeadings = new(StringComparer.Ordinal)
    {
        "MCP Tool Validation",
        "Workflow MCP Tools Triggers",
    };

    /// <inheritdoc />
    public IReadOnlyList<InstructionsFileToolObligationFindingEntry> Validate(
        IReadOnlyDictionary<string, InstructionsFileParsedFile> corpus,
        IReadOnlyDictionary<string, McpToolsRegistryTool> registry)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(registry);

        var findings = new List<InstructionsFileToolObligationFindingEntry>();

        foreach (var key in corpus.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            var file = corpus[key];
            var body = file.Content.Body;

            foreach (var section in body.Sections)
            {
                if (section.Level != 2 || !ContractedHeadings.Contains(section.Heading))
                {
                    continue;
                }

                ValidateSection(key, file.FileName, body.RawValue, section.TextSpan, registry, findings);
            }
        }

        return findings;
    }

    private static void CollectFromLine(
        ReadOnlySpan<char> lineText,
        int line,
        int lineOffset,
        List<InlineCodeToken> tokens)
    {
        var index = 0;

        while (index < lineText.Length)
        {
            if (lineText[index] != '`')
            {
                index++;
                continue;
            }

            var runLength = CountBackticks(lineText, index);
            var contentStart = index + runLength;
            var close = FindClosingRun(lineText, contentStart, runLength);

            if (close < 0)
            {
                // An unmatched run is literal text, not a code span.
                index = contentStart;
                continue;
            }

            var content = lineText[contentStart..close].Trim();

            if (content.Length > 0)
            {
                tokens.Add(new InlineCodeToken(content.ToString(), line, lineOffset + contentStart));
            }

            index = close + runLength;
        }
    }

    /// <summary>
    /// Gathers every inline-code span inside <paramref name="span"/> of
    /// <paramref name="body"/>, one token per span, skipping fenced code blocks (a
    /// fence is a line whose first non-blank characters are three backticks). A run
    /// of <c>n</c> backticks opens a span that only a run of exactly <c>n</c>
    /// backticks on the same line closes, which is how the syntax parser masks
    /// inline code before it looks for references.
    /// </summary>
    private static List<InlineCodeToken> CollectInlineCodeTokens(string body, InstructionsFileTextSpan span)
    {
        var tokens = new List<InlineCodeToken>();
        var text = body.AsSpan(span.Range);
        var line = CountLines(body.AsSpan(0, span.StartIndex));
        var inFence = false;
        var lineStart = 0;

        while (true)
        {
            var newline = text[lineStart..].IndexOf('\n');
            var lineEnd = newline < 0 ? text.Length : lineStart + newline;
            var lineText = text[lineStart..lineEnd].TrimEnd('\r');

            if (lineText.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence)
            {
                CollectFromLine(lineText, line, span.StartIndex + lineStart, tokens);
            }

            if (newline < 0)
            {
                break;
            }

            lineStart = lineEnd + 1;
            line++;
        }

        return tokens;
    }

    private static int CountBackticks(ReadOnlySpan<char> text, int index)
    {
        var count = 0;

        while (index + count < text.Length && text[index + count] == '`')
        {
            count++;
        }

        return count;
    }

    private static int CountLines(ReadOnlySpan<char> text)
    {
        var count = 0;

        foreach (var character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string DescribeUnknownParameter(string token, List<McpToolsRegistryTool> namedTools)
    {
        if (namedTools.Count == 0)
        {
            return "`" + token + "` reads as a parameter but the section names no registered MCP tool";
        }

        var names = string.Join(", ", namedTools.Select(static tool => "`" + tool.Name + "`"));

        return "`" + token + "` is not a parameter of " + names;
    }

    private static int FindClosingRun(ReadOnlySpan<char> text, int from, int runLength)
    {
        var index = from;

        while (index < text.Length)
        {
            if (text[index] != '`')
            {
                index++;
                continue;
            }

            var count = CountBackticks(text, index);

            if (count == runLength)
            {
                return index;
            }

            index += count;
        }

        return -1;
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex GeneratedParameterNameRegex();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)+$")]
    private static partial Regex GeneratedToolNameRegex();

    /// <summary>
    /// Resolves one contracted section in two passes: the tool-shaped tokens first,
    /// because they decide which parameters the section may cite, then the
    /// parameter-shaped tokens against the union of those tools' parameters. When
    /// the section names tools but none of them is registered, the parameter pass
    /// is skipped: the unknown tool is the root cause, and reporting every
    /// parameter beside it as "names no registered tool" would bury it. Findings
    /// are appended in body order so a report reads top to bottom.
    /// </summary>
    private static void ValidateSection(
        string key,
        string fileName,
        string body,
        InstructionsFileTextSpan span,
        IReadOnlyDictionary<string, McpToolsRegistryTool> registry,
        List<InstructionsFileToolObligationFindingEntry> findings)
    {
        var tokens = CollectInlineCodeTokens(body, span);
        var namedTools = new List<McpToolsRegistryTool>();
        var unknownToolCount = 0;
        var sectionFindings = new List<(int Offset, InstructionsFileToolObligationFindingEntry Entry)>();

        foreach (var token in tokens)
        {
            if (!GeneratedToolNameRegex().IsMatch(token.Text))
            {
                continue;
            }

            if (registry.TryGetValue(token.Text, out var tool))
            {
                namedTools.Add(tool);
            }
            else
            {
                unknownToolCount++;
                sectionFindings.Add((token.Offset, new InstructionsFileToolObligationFindingEntry(
                    key,
                    fileName,
                    InstructionsFileToolObligationFindingKind.UnknownTool,
                    token.Text,
                    token.Line,
                    "`" + token.Text + "` is not a registered MCP tool")));
            }
        }

        var parametersResolvable = namedTools.Count > 0 || unknownToolCount == 0;

        foreach (var token in tokens)
        {
            if (!parametersResolvable
                || !GeneratedParameterNameRegex().IsMatch(token.Text)
                || namedTools.Any(tool => tool.ParameterNames.Contains(token.Text)))
            {
                continue;
            }

            sectionFindings.Add((token.Offset, new InstructionsFileToolObligationFindingEntry(
                key,
                fileName,
                InstructionsFileToolObligationFindingKind.UnknownParameter,
                token.Text,
                token.Line,
                DescribeUnknownParameter(token.Text, namedTools))));
        }

        findings.AddRange(sectionFindings.OrderBy(static finding => finding.Offset).Select(static finding => finding.Entry));
    }

    private readonly record struct InlineCodeToken(string Text, int Line, int Offset);
}
