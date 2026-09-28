using System.Runtime.InteropServices;
using Autarx.Core.Update;

namespace Autarx.Cli;

internal static class UpdateCommand
{
    /// <summary>`autarx update` — check the release feed and self-apply newer
    /// builds. Exit 0 up-to-date/applied, 1 update available (with --check),
    /// 2 operational failure.</summary>
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var checkOnly, out var force, out var feed, out var kind, out var platform, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var currentVersion = Program.Version();
        UpdateCheck check;
        try
        {
            check = UpdateService.CheckAsync(feed, currentVersion, kind, platform).GetAwaiter().GetResult();
        }
        catch (UpdateException ex)
        {
            Console.Error.WriteLine($"autarx: update check failed: {ex.Message}");
            return 2;
        }

        if (json && (checkOnly || !check.UpdateAvailable))
        {
            JsonOutput.Write(new CheckJson(currentVersion, check.LatestVersion, check.UpdateAvailable, null, null));
            return check.UpdateAvailable ? 1 : 0;
        }

        if (!check.UpdateAvailable)
        {
            Console.WriteLine($"autarx {currentVersion} is up to date (latest release: {check.LatestVersion}).");
            return 0;
        }

        if (checkOnly)
        {
            Console.WriteLine($"update available: {check.LatestVersion} (current: {currentVersion}) — run 'autarx update' to apply.");
            return 1;
        }

        Console.WriteLine($"updating: {currentVersion} → {check.LatestVersion}");
        string payloadRoot;
        try
        {
            Console.WriteLine("  downloading and verifying…");
            payloadRoot = UpdateService.DownloadAndExtractAsync(check.Asset!, Path.Combine(Path.GetTempPath(), "autarx-update"), MarkerFileName(kind)).GetAwaiter().GetResult();
        }
        catch (UpdateException ex)
        {
            Console.Error.WriteLine($"autarx: update failed: {ex.Message}");
            return 2;
        }

        var appDirectory = AppContext.BaseDirectory;
        if (!force && !Directory.Exists(appDirectory))
        {
            Console.Error.WriteLine($"autarx: cannot locate install directory: {appDirectory}");
            return 2;
        }

        int replaced;
        try
        {
            replaced = UpdateInstaller.Apply(payloadRoot, appDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"autarx: apply failed: {ex.Message}");
            Console.Error.WriteLine("autarx: the previous binary was kept as <file>.autarx-update.old and can be restored manually.");
            return 2;
        }

        Console.WriteLine($"  applied {replaced} file(s) to {appDirectory}");
        Console.WriteLine($"autarx {check.LatestVersion} installed — restart to take effect.");

        if (json)
            JsonOutput.Write(new CheckJson(currentVersion, check.LatestVersion, true, replaced, appDirectory));
        return 0;
    }

    /// <summary>Verifies the feed/apply round trip against a local package
    /// layout — used by tests and CI smoke with a file:// style feed path.</summary>
    private static string MarkerFileName(string kind) =>
        kind == "gui" ? "Autarx.Gui.exe" : "autarx.exe";

    private static string DefaultPlatform()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64",
        };
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
            : "linux";
        return $"{os}-{arch}";
    }

    internal sealed record CheckJson(
        string CurrentVersion,
        string LatestVersion,
        bool UpdateAvailable,
        int? FilesReplaced,
        string? InstallDirectory);

    private static bool TryParseArgs(
        string[] args,
        out bool checkOnly,
        out bool force,
        out string feed,
        out string kind,
        out string platform,
        out bool json,
        out string? error)
    {
        checkOnly = false;
        force = false;
        feed = UpdateFeed.DefaultFeedUrl;
        kind = "cli";
        platform = "";
        json = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--check")
            {
                checkOnly = true;
            }
            else if (arg == "--force")
            {
                force = true;
            }
            else if (arg == "--json")
            {
                json = true;
            }
            else if (arg == "--feed")
            {
                if (i + 1 >= args.Length) { error = "autarx: --feed requires a URL or file path"; return false; }
                feed = args[++i];
            }
            else if (arg == "--kind")
            {
                if (i + 1 >= args.Length || args[++i] is not ("cli" or "gui"))
                {
                    error = "autarx: --kind must be cli or gui";
                    return false;
                }
                kind = args[i];
            }
            else if (arg == "--platform")
            {
                if (i + 1 >= args.Length) { error = "autarx: --platform requires a value (e.g. windows-x64)"; return false; }
                platform = args[++i];
            }
            else if (arg.StartsWith('-'))
            {
                error = $"autarx: unknown option '{arg}' — try 'autarx update --help'";
                return false;
            }
            else
            {
                error = "autarx: update takes no positional arguments";
                return false;
            }
        }

        if (platform.Length == 0)
            platform = DefaultPlatform();

        return error is null;
    }
}
