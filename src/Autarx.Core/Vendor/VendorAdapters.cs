namespace Autarx.Core.Vendor;

/// <summary>
/// Adapters for the three vendor generators Autarx hands off to. Detection
/// order: the tool's conventional HOME env var, then PATH. Default argument
/// templates are deliberately conservative (project path only) and are meant
/// to be overridden per project when the vendor CLI needs more — the
/// templates are a convenience, not a claim about vendor CLI contracts.
/// </summary>
public static class VendorAdapters
{
    public static IReadOnlyList<IVendorAdapter> All { get; } =
    [
        new GenericAdapter("davinci", "DaVinci Configurator", "DAVINCI_HOME",
            ["DaVinciConfigurator.exe", "dvcmd.exe"]),
        new GenericAdapter("tresos", "EB tresos Studio", "TRESOS_HOME",
            ["tresos_cmd.bat", "tresos_studio.bat", "tresos.bat"]),
        new GenericAdapter("isolar", "ETAS ISOLAR", "ISOLAR_HOME",
            ["isolar.exe", "ISOLAR-B.exe"]),
    ];

    public static IVendorAdapter? Find(string name) =>
        All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Shared detection/hand-off behaviour: HOME env var probes (root and
    /// bin), then PATH. Works identically for every vendor family.
    /// </summary>
    public sealed class GenericAdapter(
        string name,
        string displayName,
        string homeVariable,
        IReadOnlyList<string> executableNames) : IVendorAdapter
    {
        public string Name => name;

        public string DisplayName => displayName;

        public VendorToolInfo? Detect(Func<string, string?> environmentLookup, Func<string, string?> pathResolver)
        {
            var home = environmentLookup(homeVariable);
            foreach (var executable in executableNames)
            {
                if (!string.IsNullOrEmpty(home))
                {
                    foreach (var candidate in new[]
                             {
                                 Path.Combine(home, "bin", executable),
                                 Path.Combine(home, executable),
                             })
                    {
                        var resolved = pathResolver(candidate);
                        if (resolved is not null)
                            return new VendorToolInfo(name, resolved, null);
                    }
                }

                var onPath = pathResolver(executable);
                if (onPath is not null)
                    return new VendorToolInfo(name, onPath, null);
            }

            return null;
        }

        public VendorInvocation BuildInvocation(
            VendorToolInfo tool,
            string projectPath,
            string? argumentsTemplate,
            IReadOnlyList<string> extraArguments)
        {
            var fullProject = Path.GetFullPath(projectPath);
            var arguments = argumentsTemplate is { Length: > 0 }
                ? argumentsTemplate.Replace("{project}", fullProject)
                : $"\"{fullProject}\"";

            if (extraArguments.Count > 0)
                arguments += " " + string.Join(" ", extraArguments);

            return new VendorInvocation(tool.ExecutablePath, arguments, Path.GetDirectoryName(fullProject) ?? ".");
        }
    }
}
