using System.Net;

namespace Autarx.Core.Update;

public sealed record UpdateCheck(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    UpdateAsset? Asset)
{
    public static UpdateCheck UpToDate(string currentVersion, string latestVersion) =>
        new(currentVersion, latestVersion, false, null);
}

/// <summary>
/// Shared orchestration for the CLI `autarx update` command and the GUI
/// update menu: check the feed, decide, download, verify. The swap itself is
/// <see cref="UpdateInstaller.Apply"/> — hosts call it only after the
/// checksum verified.
/// </summary>
public static class UpdateService
{
    public static async Task<UpdateCheck> CheckAsync(
        string feed,
        string currentVersion,
        string kind,
        string platform,
        CancellationToken cancellationToken = default)
    {
        var manifest = await UpdateFeed.LoadAsync(feed, cancellationToken);
        var asset = manifest.SelectAsset(kind, platform);
        var available = asset is not null
            && UpdateFeed.IsNewer(currentVersion, manifest.Version);
        return new UpdateCheck(currentVersion, manifest.Version, available, asset);
    }

    /// <summary>Downloads the asset into <paramref name="destinationDirectory"/>,
    /// verifies its hash and expands it. Returns the payload root directory —
    /// the folder inside the archive that contains <paramref name="markerFileName"/>
    /// (autarx.exe for the CLI, Autarx.Gui.exe for the GUI).</summary>
    public static async Task<string> DownloadAndExtractAsync(
        UpdateAsset asset,
        string destinationDirectory,
        string markerFileName,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var fileName = Path.GetFileName(new Uri(asset.Url).LocalPath);
        if (string.IsNullOrEmpty(fileName))
            fileName = "autarx-update.zip";
        var zipPath = Path.Combine(destinationDirectory, fileName);

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("autarx-update");
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(zipPath);
            await source.CopyToAsync(target, cancellationToken);
        }
        catch (Exception ex) when (ex is not UpdateException)
        {
            throw new UpdateException($"download failed: {ex.Message}", ex);
        }

        return UpdateInstaller.ExtractVerified(zipPath, asset.Sha256, markerFileName);
    }
}
