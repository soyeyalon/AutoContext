namespace AutoContext.Instructions.Manifest.Generator.Tests.Support;

using System.Text;
using System.Text.Json;

using AutoContext.Instructions.Manifest.Generator;

/// <summary>
/// Writes an <c>mcp-tools-registry.json</c> into a temp directory so the
/// <see cref="McpToolsRegistryReader"/> can read it back, mirroring the way
/// <see cref="InstructionsCatalogTestWriter"/> seeds a catalog on disk. Only the
/// fields the reader looks at are meaningful; the rest are filled with placeholder
/// values so the file still has the registry's shape.
/// </summary>
internal static class McpToolsRegistryTestWriter
{
    private const string RegistryFileName = "mcp-tools-registry.json";

    internal static string Write(string directory, params (string Name, string[] Parameters)[] tools)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", "1");
            writer.WriteStartArray("tools");

            foreach (var (name, parameters) in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("workerId", "test");
                writer.WriteString("description", "Test tool " + name + ".");
                writer.WriteStartObject("parameters");

                foreach (var parameter in parameters)
                {
                    writer.WriteStartObject(parameter);
                    writer.WriteString("type", "string");
                    writer.WriteString("description", "Test parameter " + parameter + ".");
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WriteRaw(directory, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    internal static string WriteRaw(string directory, string json)
    {
        var path = Path.Combine(directory, RegistryFileName);
        File.WriteAllText(path, json);
        return path;
    }
}
