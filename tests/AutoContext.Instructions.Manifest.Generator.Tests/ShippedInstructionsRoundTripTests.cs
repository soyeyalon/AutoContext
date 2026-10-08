namespace AutoContext.Instructions.Manifest.Generator.Tests;

using System.Text.RegularExpressions;

using AutoContext.Engine.Tests.Support.Diagnostics;
using AutoContext.Instructions.Manifest.Generator;
using AutoContext.Instructions.Parser;

public sealed partial class ShippedInstructionsRoundTripTests
{
    public sealed class Build
    {
        private readonly CorpusParser _corpusParser = new();
        private readonly InstructionsManifestBuilder _sut = new();

        [Fact]
        public async Task Should_build_one_entry_for_every_shipped_file()
        {
            // Arrange
            var instructionsPath = EngineInstructionsPath.Value;
            var expectedCount = Directory.GetFiles(instructionsPath, "*.instructions.md").Length;

            // Act
            var manifest = _sut.Build(await _corpusParser.ParseAsync(instructionsPath, TestContext.Current.CancellationToken));

            // Assert
            Assert.Multiple(
                () => Assert.NotEmpty(manifest.Instructions),
                () => Assert.Equal(expectedCount, manifest.Instructions.Count));
        }

        [Fact]
        public async Task Should_carry_section_maps_and_extensions_for_the_shipped_files()
        {
            // Act
            var manifest = _sut.Build(await _corpusParser.ParseAsync(EngineInstructionsPath.Value, TestContext.Current.CancellationToken));

            // Assert
            Assert.Multiple(
                () => Assert.Contains(manifest.Instructions, static entry => entry.Sections.Count > 0),
                () => Assert.Contains(manifest.Instructions, static entry => entry.Extensions is { Count: > 0 }));
        }
    }

    public sealed class ApplyTo
    {
        [Fact]
        public async Task Should_round_trip_every_shipped_value_verbatim()
        {
            // Arrange
            var files = Directory.GetFiles(EngineInstructionsPath.Value, "*.instructions.md");

            // Act
            var nonRoundTripping = new List<string?>();
            foreach (var path in files)
            {
                var parsed = await InstructionsFileFactory.FromFileAsync(path, TestContext.Current.CancellationToken);
                if (parsed.Frontmatter.ApplyTo is { RoundTrips: false })
                {
                    nonRoundTripping.Add(Path.GetFileName(path));
                }
            }

            // Assert
            Assert.Multiple(
                () => Assert.NotEmpty(files),
                () => Assert.Empty(nonRoundTripping));
        }
    }

    public sealed class Catalog
    {
        private readonly CorpusParser _corpusParser = new();
        private readonly InstructionsCatalogReader _sut = new();

        [Fact]
        public async Task Should_reconcile_the_shipped_catalog_with_the_corpus()
        {
            // Arrange
            var corpus = await _corpusParser.ParseAsync(EngineInstructionsPath.Value, TestContext.Current.CancellationToken);
            var catalogPath = Path.Combine(
                Path.GetDirectoryName(EngineInstructionsPath.Value)!,
                "Resources",
                "instructions-catalog.json");

            // Act
            var exception = Record.Exception(() => _sut.Read(catalogPath, corpus));

            // Assert
            Assert.Null(exception);
        }
    }

    public sealed partial class WorkerRuleIds
    {
        private readonly CorpusParser _corpusParser = new();

        [Fact]
        public async Task Should_resolve_every_rule_id_the_workers_report()
        {
            // Arrange — a finding's rule id is what a user disables and what a hook matches
            // on, so it must name a real rule: the worker tags once drifted onto
            // neighbouring rule numbers when the corpus was renumbered, unnoticed.
            var corpus = await _corpusParser.ParseAsync(EngineInstructionsPath.Value, TestContext.Current.CancellationToken);
            var ruleIds = corpus.Values
                .SelectMany(static file => file.Content.Body.Rules
                    .Where(static rule => rule.Id is not null)
                    .Select(rule => file.FileName[..^".instructions.md".Length] + "#" + rule.Id))
                .ToHashSet(StringComparer.Ordinal);
            var cited = ReadWorkerRuleIds();

            // Act
            var unresolved = cited.Where(id => !ruleIds.Contains(id)).Order(StringComparer.Ordinal).ToList();

            // Assert
            Assert.Multiple(
                () => Assert.NotEmpty(cited),
                () => Assert.Empty(unresolved));
        }

        private static HashSet<string> ReadWorkerRuleIds()
        {
            var sourceRoots = new[]
            {
                Path.Combine(RepositoryRoot.Value, "src", "AutoContext.Worker.DotNet", "Tasks"),
                Path.Combine(RepositoryRoot.Value, "src", "AutoContext.Worker.Web", "src"),
            };

            return sourceRoots
                .Where(Directory.Exists)
                .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
                .Where(static path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".ts", StringComparison.Ordinal))
                .SelectMany(static path => RuleIdLiteralRegex().Matches(File.ReadAllText(path)).Select(static match => match.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);
        }

        [GeneratedRegex("""["']([a-z][a-z0-9-]*#INST\d{4})["']""")]
        private static partial Regex RuleIdLiteralRegex();
    }

    public sealed class ToolObligations
    {
        private readonly CorpusParser _corpusParser = new();
        private readonly McpToolsRegistryReader _registryReader = new();
        private readonly InstructionsToolObligationValidator _sut = new();

        [Fact]
        public async Task Should_validate_the_shipped_corpus_against_the_shipped_registry()
        {
            // Arrange — the regression that catches the next tool or parameter rename.
            var corpus = await _corpusParser.ParseAsync(EngineInstructionsPath.Value, TestContext.Current.CancellationToken);
            var registry = _registryReader.Read(Path.Combine(EngineResourcesPath.Value, "mcp-tools-registry.json"));

            // Act
            var findings = _sut.Validate(corpus, registry);

            // Assert
            Assert.Multiple(
                () => Assert.NotEmpty(registry),
                () => Assert.Empty(findings.Select(static finding => $"{finding.SourceFileName}:{finding.Line + 1} {finding.Message}")));
        }
    }
}
