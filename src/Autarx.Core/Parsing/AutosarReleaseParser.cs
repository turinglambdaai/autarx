using System.Text.RegularExpressions;

namespace Autarx.Core.Parsing;

/// <summary>
/// Derives the AUTOSAR release from a xsi:schemaLocation value when the
/// pattern is recognisable. Never guesses: unrecognised forms yield null.
/// </summary>
public static partial class AutosarReleaseParser
{
    // AUTOSAR_4-3-0.xsd / AUTOSAR_4-3-0
    [GeneratedRegex(@"AUTOSAR_(\d{1,2}-\d{1,2}(?:-\d{1,2})?)(?:\.xsd)?", RegexOptions.CultureInvariant)]
    private static partial Regex ClassicPattern();

    // AUTOSAR_R23-11 (found inside AUTOSAR_R23-11.xsd or similar)
    [GeneratedRegex(@"AUTOSAR_(R\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex FoundryPattern();

    // AUTOSAR_00050.xsd — post-2023 numeric schema identifiers, reported verbatim
    [GeneratedRegex(@"AUTOSAR_(\d{5})(?:\.xsd)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumericPattern();

    public static string? Derive(string? schemaLocation)
    {
        if (string.IsNullOrEmpty(schemaLocation))
            return null;

        var classic = ClassicPattern().Match(schemaLocation);
        if (classic.Success)
            return classic.Groups[1].Value.Replace('-', '.');

        var foundry = FoundryPattern().Match(schemaLocation);
        if (foundry.Success)
            return foundry.Groups[1].Value;

        var numeric = NumericPattern().Match(schemaLocation);
        if (numeric.Success)
            return numeric.Groups[1].Value;

        return null;
    }
}
