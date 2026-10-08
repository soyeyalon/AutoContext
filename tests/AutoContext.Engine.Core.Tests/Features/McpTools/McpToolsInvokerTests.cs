namespace AutoContext.Engine.Core.Tests.Features.McpTools;

using System.Text.Json;

using AutoContext.Engine.Core.Features.McpTools;
using AutoContext.Engine.Protocol.Messages.McpTools;

public sealed class McpToolsInvokerTests
{
    public sealed class TryGetFilePath
    {
        [Fact]
        public void Should_return_the_file_path_when_present()
        {
            // Arrange
            var arguments = JsonDocument.Parse(
                """{"content":"x","filePath":"/repo/File.cs"}""").RootElement;

            // Act
            var filePath = McpToolsInvoker.TryGetFilePath(arguments);

            // Assert
            Assert.Equal("/repo/File.cs", filePath);
        }

        [Fact]
        public void Should_return_null_when_absent()
            => Assert.Null(
                McpToolsInvoker.TryGetFilePath(JsonDocument.Parse("""{"content":"x"}""").RootElement));

        [Fact]
        public void Should_return_null_when_blank()
            => Assert.Null(
                McpToolsInvoker.TryGetFilePath(JsonDocument.Parse("""{"filePath":"   "}""").RootElement));

        [Fact]
        public void Should_return_null_when_not_a_string()
            => Assert.Null(
                McpToolsInvoker.TryGetFilePath(JsonDocument.Parse("""{"filePath":42}""").RootElement));

        [Fact]
        public void Should_return_null_when_arguments_is_not_an_object()
            => Assert.Null(
                McpToolsInvoker.TryGetFilePath(JsonDocument.Parse("\"scalar\"").RootElement));
    }

    public sealed class BuildRequestBytes
    {
        [Fact]
        public void Should_inject_the_resolved_editorconfig_map()
        {
            // Arrange
            var arguments = JsonDocument.Parse("""{"content":"class C {}"}""").RootElement;
            var editorconfig = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["csharp_prefer_braces"] = "true",
                ["indent_size"] = "4",
            };

            // Act
            var bytes = McpToolsInvoker.BuildRequestBytes(
                "analyze_csharp_code_style", arguments, editorconfig, "abc123");

            // Assert
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var editorconfigElement = root.GetProperty("editorconfig");

            Assert.Multiple(
                () => Assert.Equal(
                    "analyze_csharp_code_style", root.GetProperty("mcpTask").GetString()),
                () => Assert.Equal("abc123", root.GetProperty("correlationId").GetString()),
                () => Assert.Equal(
                    "class C {}", root.GetProperty("data").GetProperty("content").GetString()),
                () => Assert.Equal(
                    "true", editorconfigElement.GetProperty("csharp_prefer_braces").GetString()),
                () => Assert.Equal(
                    "4", editorconfigElement.GetProperty("indent_size").GetString()));
        }

        [Fact]
        public void Should_emit_an_empty_editorconfig_object_when_the_map_is_empty()
        {
            // Arrange
            var arguments = JsonDocument.Parse("""{"content":"class C {}"}""").RootElement;

            // Act
            var bytes = McpToolsInvoker.BuildRequestBytes(
                "analyze_csharp_code_style",
                arguments,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "abc123");

            // Assert
            using var document = JsonDocument.Parse(bytes);
            var editorconfigElement = document.RootElement.GetProperty("editorconfig");

            Assert.Multiple(
                () => Assert.Equal(JsonValueKind.Object, editorconfigElement.ValueKind),
                () => Assert.Empty(editorconfigElement.EnumerateObject()));
        }
    }

    public sealed class ComposeResult
    {
        private const string ToolName = "analyze_sample";

        [Fact]
        public void Should_pass_a_single_ok_reply_through()
        {
            // Arrange
            var responses = new[] { Ok("task_a", """{"passed":false,"report":"❌ one"}""") };

            // Act
            var result = McpToolsInvoker.ComposeResult(ToolName, responses);

            // Assert
            var ok = Assert.IsType<JsonMcpToolsInvokeOkResult>(result);
            var report = ReadSingleTextAsJson(ok.Content);
            Assert.Multiple(
                () => Assert.Equal(ToolName, ok.Name),
                () => Assert.False(report.GetProperty("passed").GetBoolean()),
                () => Assert.Equal("❌ one", report.GetProperty("report").GetString()));
        }

        [Fact]
        public void Should_turn_a_single_error_reply_into_a_tool_error()
        {
            // Arrange
            var responses = new[] { new McpToolsWorkerTaskResponse("task_a", "error", null, "Unknown task 'task_a'.") };

            // Act
            var result = McpToolsInvoker.ComposeResult(ToolName, responses);

            // Assert
            var error = Assert.IsType<JsonMcpToolsInvokeToolErrorResult>(result);
            Assert.Equal("Unknown task 'task_a'.", ReadSingleText(error.Content));
        }

        [Fact]
        public void Should_merge_reports_and_pass_only_when_every_task_passed()
        {
            // Arrange
            var responses = new[]
            {
                Ok("task_a", """{"passed":true,"report":"✅ first"}"""),
                Ok("task_b", """{"passed":false,"report":"❌ second"}"""),
            };

            // Act
            var result = McpToolsInvoker.ComposeResult(ToolName, responses);

            // Assert
            var ok = Assert.IsType<JsonMcpToolsInvokeOkResult>(result);
            var report = ReadSingleTextAsJson(ok.Content);
            Assert.Multiple(
                () => Assert.False(report.GetProperty("passed").GetBoolean()),
                () => Assert.Equal("✅ first\n\n❌ second", report.GetProperty("report").GetString()));
        }

        [Fact]
        public void Should_concatenate_structured_findings_in_task_order()
        {
            // Arrange
            var responses = new[]
            {
                Ok("task_a", """{"passed":false,"report":"❌ a","findings":[{"ruleId":"lang-csharp#INST0005","severity":"violation","line":3,"message":"a"}]}"""),
                Ok("task_b", """{"passed":true,"report":"✅ b","findings":[]}"""),
                Ok("task_c", """{"passed":true,"report":"✅ c","findings":[{"ruleId":"lang-csharp#INST0015","severity":"suggestion","message":"c"}]}"""),
            };

            // Act
            var result = McpToolsInvoker.ComposeResult(ToolName, responses);

            // Assert
            var findings = ReadSingleTextAsJson(Assert.IsType<JsonMcpToolsInvokeOkResult>(result).Content)
                .GetProperty("findings")
                .EnumerateArray()
                .Select(static finding => finding.GetProperty("ruleId").GetString())
                .ToList();
            Assert.Equal(["lang-csharp#INST0005", "lang-csharp#INST0015"], findings);
        }

        [Fact]
        public void Should_pass_a_merged_report_when_every_task_passed()
        {
            // Arrange
            var responses = new[]
            {
                Ok("task_a", """{"passed":true,"report":"✅ first"}"""),
                Ok("task_b", """{"passed":true,"report":"✅ second"}"""),
            };

            // Act
            var result = McpToolsInvoker.ComposeResult(ToolName, responses);

            // Assert
            var report = ReadSingleTextAsJson(Assert.IsType<JsonMcpToolsInvokeOkResult>(result).Content);
            Assert.True(report.GetProperty("passed").GetBoolean());
        }

        [Fact]
        public void Should_fail_the_tool_and_name_every_failed_task()
        {
            // Arrange — a report missing one of its checks must not read as clean.
            var responses = new[]
            {
                Ok("task_a", """{"passed":true,"report":"✅ first"}"""),
                new McpToolsWorkerTaskResponse("task_b", "error", null, "boom"),
                new McpToolsWorkerTaskResponse("task_c", null, null, null),
            };

            // Act
            var result = McpToolsInvoker.ComposeResult(ToolName, responses);

            // Assert
            var text = ReadSingleText(Assert.IsType<JsonMcpToolsInvokeToolErrorResult>(result).Content);
            Assert.Multiple(
                () => Assert.Contains("Task 'task_b' failed: boom", text, StringComparison.Ordinal),
                () => Assert.Contains("Task 'task_c' failed: Worker returned unknown status '(missing)'.", text, StringComparison.Ordinal),
                () => Assert.DoesNotContain("task_a", text, StringComparison.Ordinal));
        }

        [Fact]
        public void Should_keep_outputs_that_are_not_reports_apart_in_task_order()
        {
            // Arrange
            var responses = new[]
            {
                Ok("task_a", """{"indent_size":"4"}"""),
                Ok("task_b", """{"passed":true,"report":"✅ second"}"""),
            };

            // Act
            var result = McpToolsInvoker.ComposeResult(ToolName, responses);

            // Assert
            var ok = Assert.IsType<JsonMcpToolsInvokeOkResult>(result);
            Assert.Collection(
                ok.Content,
                first => Assert.Contains("indent_size", first.GetProperty("text").GetString(), StringComparison.Ordinal),
                second => Assert.Contains("✅ second", second.GetProperty("text").GetString(), StringComparison.Ordinal));
        }

        [Fact]
        public void Should_reject_an_empty_reply_list()
        {
            // Act + Assert
            Assert.Throws<ArgumentException>(() => McpToolsInvoker.ComposeResult(ToolName, []));
        }

        private static McpToolsWorkerTaskResponse Ok(string taskName, string outputJson)
            => new(taskName, "ok", JsonDocument.Parse(outputJson).RootElement.Clone(), null);

        private static string ReadSingleText(IReadOnlyList<JsonElement> content)
            => Assert.Single(content).GetProperty("text").GetString()!;

        private static JsonElement ReadSingleTextAsJson(IReadOnlyList<JsonElement> content)
            => JsonDocument.Parse(ReadSingleText(content)).RootElement.Clone();
    }
}
