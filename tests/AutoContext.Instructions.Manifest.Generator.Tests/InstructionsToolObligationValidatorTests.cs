namespace AutoContext.Instructions.Manifest.Generator.Tests;

using AutoContext.Engine.Tests.Support.IO;
using AutoContext.Instructions.Manifest.Generator;
using AutoContext.Instructions.Manifest.Generator.Tests.Support;

public sealed class InstructionsToolObligationValidatorTests
{
    public sealed class Validate(TempDirectoryFixture tempDirectory) : IClassFixture<TempDirectoryFixture>
    {
        private const string ToolSection = "# Title\n\n## MCP Tool Validation\n\n";

        private readonly CorpusParser _corpusParser = new();
        private readonly InstructionsToolObligationValidator _sut = new();

        [Fact]
        public void Should_reject_null_corpus()
        {
            // Act + Assert
            Assert.Throws<ArgumentNullException>(() => _sut.Validate(null!, CreateRegistry()));
        }

        [Fact]
        public async Task Should_reject_null_registry()
        {
            // Arrange
            var corpus = await _corpusParser.ParseAsync(tempDirectory.CreateDirectory(), TestContext.Current.CancellationToken);

            // Act + Assert
            Assert.Throws<ArgumentNullException>(() => _sut.Validate(corpus, null!));
        }

        [Fact]
        public async Task Should_yield_no_findings_when_every_token_resolves()
        {
            // Arrange
            var corpus = await WriteAndParseAsync(
                ToolSection + "Call `analyze_csharp_code_style` with `content` and `filePath`.\n");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry(("analyze_csharp_code_style", ["content", "filePath"])));

            // Assert
            Assert.Empty(findings);
        }

        [Fact]
        public async Task Should_flag_an_unregistered_tool()
        {
            // Arrange
            var corpus = await WriteAndParseAsync(ToolSection + "Call `analyze_csharp_code` with `content`.\n");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry(("analyze_csharp_code_style", ["content"])));

            // Assert
            var finding = Assert.Single(findings);
            Assert.Multiple(
                () => Assert.Equal("lang-csharp", finding.SourceKey),
                () => Assert.Equal("lang-csharp.instructions.md", finding.SourceFileName),
                () => Assert.Equal(InstructionsFileToolObligationFindingKind.UnknownTool, finding.Kind),
                () => Assert.Equal("analyze_csharp_code", finding.Token),
                () => Assert.Equal(4, finding.Line),
                () => Assert.Equal("`analyze_csharp_code` is not a registered MCP tool", finding.Message));
        }

        [Fact]
        public async Task Should_not_cascade_parameter_findings_onto_an_unregistered_tool()
        {
            // Arrange — the stale tool is the root cause; its parameters are not separately wrong.
            var corpus = await WriteAndParseAsync(
                ToolSection + "Call `analyze_csharp_code` with `content` and `originalPath`.\n");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry(("analyze_csharp_code_style", ["content", "filePath"])));

            // Assert
            var finding = Assert.Single(findings);
            Assert.Equal(InstructionsFileToolObligationFindingKind.UnknownTool, finding.Kind);
        }

        [Fact]
        public async Task Should_flag_a_parameter_the_named_tool_does_not_declare()
        {
            // Arrange
            var corpus = await WriteAndParseAsync(
                ToolSection + "Call `analyze_csharp_code_style` with `content` and `originalPath`.\n");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry(("analyze_csharp_code_style", ["content", "filePath"])));

            // Assert
            var finding = Assert.Single(findings);
            Assert.Multiple(
                () => Assert.Equal(InstructionsFileToolObligationFindingKind.UnknownParameter, finding.Kind),
                () => Assert.Equal("originalPath", finding.Token),
                () => Assert.Equal("`originalPath` is not a parameter of `analyze_csharp_code_style`", finding.Message));
        }

        [Fact]
        public async Task Should_flag_a_parameter_whose_tool_is_named_only_in_another_section()
        {
            // Arrange — the triggers table names the tool; the validation section cites its parameter alone.
            var corpus = await WriteAndParseAsync(
                "# Title\n\n## Workflow MCP Tools Triggers\n\n| Trigger | Call |\n|---|---|\n| Editing | `analyze_csharp_code_style` |\n\n"
                + "## MCP Tool Validation\n\nPass the file as `content`.\n");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry(("analyze_csharp_code_style", ["content"])));

            // Assert
            var finding = Assert.Single(findings);
            Assert.Multiple(
                () => Assert.Equal(InstructionsFileToolObligationFindingKind.UnknownParameter, finding.Kind),
                () => Assert.Equal("`content` reads as a parameter but the section names no registered MCP tool", finding.Message));
        }

        [Fact]
        public async Task Should_ignore_tokens_outside_the_contract_by_shape()
        {
            // Arrange — extensions, commands, element names, and mixed-case identifiers carry no obligation.
            var corpus = await WriteAndParseAsync(
                ToolSection + "For `.cs` and `.csproj` files run `git diff --cached`, check `PackageReference`, see `Some-Thing`.\n");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry());

            // Assert
            Assert.Empty(findings);
        }

        [Fact]
        public async Task Should_ignore_fenced_code_inside_the_section()
        {
            // Arrange
            var corpus = await WriteAndParseAsync(
                ToolSection + "Example:\n\n```json\n{ \"tool\": `nope_tool` }\n```\n\nCall `analyze_csharp_code_style`.\n");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry(("analyze_csharp_code_style", [])));

            // Assert
            Assert.Empty(findings);
        }

        [Fact]
        public async Task Should_ignore_sections_outside_the_contract()
        {
            // Arrange — the same stale token under a rules heading, and under a level-3 heading of the contracted name.
            var corpus = await WriteAndParseAsync(
                "# Title\n\n## Rules\n\n- [INST0001] **Do** call `nope_tool`.\n\n### MCP Tool Validation\n\nCall `nope_tool`.\n");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry());

            // Assert
            Assert.Empty(findings);
        }

        [Fact]
        public async Task Should_scan_the_workflow_triggers_table()
        {
            // Arrange
            var corpus = await WriteAndParseAsync(
                "# Title\n\n## Workflow MCP Tools Triggers\n\n| Trigger | Call |\n|---|---|\n| Formatting | `read_editorconfig` |\n",
                "copilot.instructions.md");

            // Act
            var findings = _sut.Validate(corpus, CreateRegistry(("read_editorconfig_rules", ["filePath"])));

            // Assert
            var finding = Assert.Single(findings);
            Assert.Multiple(
                () => Assert.Equal("copilot", finding.SourceKey),
                () => Assert.Equal(InstructionsFileToolObligationFindingKind.UnknownTool, finding.Kind),
                () => Assert.Equal("read_editorconfig", finding.Token));
        }

        [Fact]
        public async Task Should_order_findings_by_file_then_by_body_position()
        {
            // Arrange — one registered tool per file, so every token is judged on its own.
            var corpus = tempDirectory.CreateDirectory();
            InstructionsCorpusTestWriter.WriteInstruction(
                corpus, "lang-typescript.instructions.md", "lang-typescript (v1.0.0)", "TS.", body: ToolSection + "Call `nope_ts` with `content`, via `analyze_typescript_code_style`.\n");
            InstructionsCorpusTestWriter.WriteInstruction(
                corpus, "lang-csharp.instructions.md", "lang-csharp (v1.0.0)", "C#.", body: ToolSection + "Pass `originalPath` to `nope_cs` (see `analyze_csharp_code_style`).\n");

            // Act
            var findings = _sut.Validate(
                await _corpusParser.ParseAsync(corpus, TestContext.Current.CancellationToken),
                CreateRegistry(("analyze_csharp_code_style", ["content"]), ("analyze_typescript_code_style", [])));

            // Assert
            Assert.Equal(
                [("lang-csharp", "originalPath"), ("lang-csharp", "nope_cs"), ("lang-typescript", "nope_ts"), ("lang-typescript", "content")],
                findings.Select(static finding => (finding.SourceKey, finding.Token)));
        }

        private static Dictionary<string, McpToolsRegistryTool> CreateRegistry(
            params (string Name, string[] Parameters)[] tools)
            => tools.ToDictionary(
                static tool => tool.Name,
                static tool => new McpToolsRegistryTool(tool.Name, new HashSet<string>(tool.Parameters, StringComparer.Ordinal)),
                StringComparer.Ordinal);

        private async Task<IReadOnlyDictionary<string, InstructionsFileParsedFile>> WriteAndParseAsync(
            string body,
            string fileName = "lang-csharp.instructions.md")
        {
            var corpus = tempDirectory.CreateDirectory();
            InstructionsCorpusTestWriter.WriteInstruction(corpus, fileName, fileName[..^".instructions.md".Length] + " (v1.0.0)", "Body.", body: body);

            return await _corpusParser.ParseAsync(corpus, TestContext.Current.CancellationToken);
        }
    }
}
