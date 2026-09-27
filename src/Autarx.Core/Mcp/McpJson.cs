using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autarx.Core.Mcp;

/// <summary>Same JSON profile as the CLI: camelCase, indented, enums as
/// camelCase strings.</summary>
public static class McpJson
{
    /// <summary>For tool payload text carried inside an envelope.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>For the JSON-RPC envelope itself: newline-delimited framing
    /// means one message MUST be exactly one line — never indented.</summary>
    public static readonly JsonSerializerOptions Envelope = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
}
