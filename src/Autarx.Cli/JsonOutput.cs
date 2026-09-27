using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autarx.Cli;

/// <summary>Single JSON profile for every command: camelCase keys, indented,
/// enums as stable camelCase strings. This output is a machine contract —
/// additive changes only.</summary>
internal static class JsonOutput
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static void Write<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, Options));
}
