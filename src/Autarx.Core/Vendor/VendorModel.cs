namespace Autarx.Core.Vendor;

/// <summary>A vendor tool found on this machine.</summary>
public sealed record VendorToolInfo(
    string Name,
    string ExecutablePath,

    /// <summary>Version when the adapter could derive it cheaply, else null —
    /// absence is reported as null, never guessed.</summary>
    string? Version);

/// <summary>The exact process Autarx would launch for a vendor hand-off.</summary>
public sealed record VendorInvocation(
    string Executable,
    string Arguments,
    string WorkingDirectory);

/// <summary>Raw process outcome of a vendor tool run.</summary>
public sealed record VendorRunResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

/// <summary>
/// Normalized relay of one vendor tool run. This is the vendor tool's own
/// result, not an Autarx verdict — Autarx adds structure (diagnostic counts,
/// severity heuristics) and preserves the raw output for audit.
/// </summary>
public sealed record VendorReport(
    string Tool,
    string Executable,
    string Arguments,
    string WorkingDirectory,
    int? ExitCode,
    bool TimedOut,
    long DurationMilliseconds,
    IReadOnlyList<VendorDiagnostic> Diagnostics,
    int ErrorCount,
    int WarningCount,
    string RawOutputFile);

public sealed record VendorDiagnostic(
    string Severity,
    string Message,
    string RawLine);
