using System.Security.Cryptography;

namespace Autarx.Core.Update;

/// <summary>
/// Feed access and version comparison. The feed source is a URL or a local
/// file path — local files keep the whole flow testable and let CI gate the
/// command without network access.
/// </summary>
public static class UpdateFeed
{
    public const string DefaultFeedUrl =
        "https://github.com/turinglambdaai/autarx/releases/latest/download/latest.json";

    /// <summary>Fetches the feed from an https URL or reads a local path.</summary>
    public static async Task<UpdateManifest> LoadAsync(string feed, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Uri.TryCreate(feed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("autarx-update");
                using var response = await client.GetAsync(uri, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException($"update feed returned {(int)response.StatusCode}");
                return UpdateManifest.Parse(body);
            }

            return UpdateManifest.Parse(await File.ReadAllTextAsync(feed, cancellationToken));
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UpdateException($"cannot read update feed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Semantic comparison when both sides parse as versions (1.0.2 &gt; 1.0.1),
    /// falling back to an ordinal inequality check so pre-release strings
    /// still surface as "different". Equality is never an update.
    /// </summary>
    public static bool IsNewer(string currentVersion, string candidateVersion)
    {
        var current = ParseLoose(currentVersion);
        var candidate = ParseLoose(candidateVersion);
        if (current is not null && candidate is not null)
            return candidate > current;
        return !string.Equals(currentVersion.TrimStart('v'), candidateVersion.TrimStart('v'), StringComparison.Ordinal);
    }

    private static Version? ParseLoose(string version)
    {
        // tolerate the leading v and a missing fourth component
        var trimmed = version.TrimStart('v', 'V');
        var core = trimmed.Split('-')[0];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }
}

/// <summary>
/// Verifies and applies an update package. Application is a swap: files
/// currently in place move to <c>&lt;name&gt;.autarx-update.old</c> (possible
/// while the executable itself is running on Windows), new files copy in,
/// stale <c>.old</c> files from previous updates are cleaned up first.
/// Nothing is touched unless the archive hash and shape check out.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Expands the update zip to a temp directory and returns the
    /// payload root — the single directory inside that contains the marker
    /// executable (the zips are packaged as <c>cli/…</c> / <c>gui/…</c>).</summary>
    public static string ExtractVerified(string zipPath, string expectedSha256, string markerFileName)
    {
        var actual = Sha256(zipPath);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException($"checksum mismatch: expected {expectedSha256}, got {actual}");

        var extractDir = Path.Combine(Path.GetTempPath(), "autarx-update-" + Guid.NewGuid().ToString("N"));
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

        var root = LocatePayloadRoot(extractDir, markerFileName);
        if (root is null)
        {
            try { Directory.Delete(extractDir, recursive: true); } catch (IOException) { }
            throw new UpdateException($"update package does not contain {markerFileName}");
        }
        return root;
    }

    /// <summary>Swaps the extracted payload over the current installation.
    /// Returns the number of files replaced/added.</summary>
    public static int Apply(string payloadRoot, string appDirectory)
    {
        CleanupStaleBackups(appDirectory);

        var replaced = 0;
        foreach (var source in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(payloadRoot, source);
            var target = Path.Combine(appDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
            {
                var backup = target + ".autarx-update.old";
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Move(target, backup);
            }

            File.Copy(source, target, overwrite: true);
            replaced++;
        }

        try { Directory.Delete(Path.GetDirectoryName(payloadRoot)!, recursive: true); } catch (IOException) { }
        return replaced;
    }

    /// <summary>Removes leftovers from previous updates. Files that cannot be
    /// deleted (image of a still-running process) are left for the next run.</summary>
    public static int CleanupStaleBackups(string appDirectory)
    {
        var removed = 0;
        foreach (var stale in Directory.EnumerateFiles(appDirectory, "*.autarx-update.old", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(stale);
                removed++;
            }
            catch (IOException)
            {
            }
        }
        return removed;
    }

    private static string? LocatePayloadRoot(string extractDir, string markerFileName)
    {
        var markers = Directory.EnumerateFiles(extractDir, markerFileName, SearchOption.AllDirectories).ToList();
        if (markers.Count == 0)
            return null;
        return Path.GetDirectoryName(markers[0]);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
