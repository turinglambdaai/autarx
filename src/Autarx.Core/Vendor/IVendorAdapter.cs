namespace Autarx.Core.Vendor;

/// <summary>
/// One vendor tool family. Adapters know how to detect an installation and
/// how to hand a project off to the tool — they never reimplement or simulate
/// the tool itself. Probes are injected so detection is testable without the
/// real tool installed.
/// </summary>
public interface IVendorAdapter
{
    string Name { get; }

    /// <summary>Human-readable tool family, e.g. "DaVinci Configurator".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Probes the machine for the tool. <paramref name="environmentLookup"/>
    /// reads env vars; <paramref name="pathResolver"/> resolves an executable
    /// candidate — a bare name to be searched on PATH, or an absolute path to
    /// be existence-checked — to a usable executable path, or null.
    /// </summary>
    VendorToolInfo? Detect(Func<string, string?> environmentLookup, Func<string, string?> pathResolver);

    /// <summary>
    /// Builds the hand-off invocation for a project. <paramref name="argumentsTemplate"/>
    /// overrides the adapter default when provided and must contain
    /// <c>{project}</c>; extra arguments are appended verbatim.
    /// </summary>
    VendorInvocation BuildInvocation(VendorToolInfo tool, string projectPath, string? argumentsTemplate, IReadOnlyList<string> extraArguments);
}
