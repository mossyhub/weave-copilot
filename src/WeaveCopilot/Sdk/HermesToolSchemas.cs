using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveCopilot.Sdk;

/// <summary>Original OpenAI declarations, never a prompt-encoded executor.</summary>
public static class HermesToolSchemas
{
    public const int MaxTools = 256;
    public const int MaxSchemaBytes = 2 * 1024 * 1024;

    public static IReadOnlyList<JsonElement> Validate(IReadOnlyList<JsonElement> schemas)
    {
        if (schemas.Count > MaxTools) throw new ArgumentException("Too many Hermes tool schemas (maximum 256)");
        long bytes = 2;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<JsonElement>();
        foreach (var schema in schemas)
        {
            bytes += System.Text.Encoding.UTF8.GetByteCount(schema.GetRawText()) + (result.Count > 0 ? 1 : 0);
            if (bytes > MaxSchemaBytes) throw new ArgumentException("Hermes tool schemas exceed 2 MiB");
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "function"
                || !schema.TryGetProperty("function", out var f) || f.ValueKind != JsonValueKind.Object
                || !f.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Expected original OpenAI function tool declaration");
            var name = n.GetString()!;
            if (name.Length is < 1 or > 64 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) || !names.Add(name))
                throw new ArgumentException("Invalid or duplicate Hermes tool name");
            if (f.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Tool description must be a string");
            if (!f.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object
                || !p.TryGetProperty("type", out var pt) || pt.ValueKind != JsonValueKind.String || pt.GetString() != "object")
                throw new ArgumentException("Tool parameters must be an object JSON schema");
            if (p.TryGetProperty("properties", out var props) && props.ValueKind != JsonValueKind.Object
                || p.TryGetProperty("required", out var required) && (required.ValueKind != JsonValueKind.Array || required.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)))
                throw new ArgumentException("Malformed tool parameter schema");
            result.Add(schema.Clone());
        }
        return result.AsReadOnly();
    }

    public static IReadOnlyList<JsonElement>? Parse(JsonNode? metadata, bool supported)
    {
        if (metadata is null) return null;
        if (metadata is not JsonObject meta) throw new ArgumentException("_meta must be an object");
        if (meta.Any(kv => kv.Key.StartsWith("hermesTool", StringComparison.Ordinal) && kv.Key != "hermesToolSchemas"))
            throw new ArgumentException("Unsupported Hermes tool metadata");
        if (!meta.ContainsKey("hermesToolSchemas")) return null;
        if (!supported) throw new ArgumentException("Hermes tool schemas require --hermes-tools-only");
        if (meta["hermesToolSchemas"] is not JsonArray array) throw new ArgumentException("hermesToolSchemas must be an array");
        if (array.Count > MaxTools || System.Text.Encoding.UTF8.GetByteCount(array.ToJsonString()) > MaxSchemaBytes)
            throw new ArgumentException("Hermes tool schema limit exceeded");
        return Validate(array.Select(n => { using var doc = JsonDocument.Parse(n?.ToJsonString() ?? "null"); return doc.RootElement.Clone(); }).ToArray());
    }
}
