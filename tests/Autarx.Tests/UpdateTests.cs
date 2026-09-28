using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Autarx.Core.Update;
using Xunit;

namespace Autarx.Tests;

public class UpdateTests : IDisposable
{
    private readonly string _dir;

    public UpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "autarx-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private const string FeedJson = """
    {
      "version": "1.0.2",
      "date": "2026-09-28T10:00:00Z",
      "notes": "https://example.com/releases/v1.0.2",
      "assets": {
        "cli": {
          "windows-x64": { "url": "https://example.com/Autarx-cli-windows-x64.zip", "sha256": "abc123", "size": 10 },
          "linux-x64": { "url": "https://example.com/Autarx-cli-linux-x64.zip", "sha256": "def456", "size": 11 }
        },
        "gui": {
          "windows-x64": { "url": "https://example.com/Autarx-gui-windows-x64.zip", "sha256": "789abc", "size": 12 }
        }
      }
    }
    """;

    [Fact]
    public void Parses_feed_and_selects_assets_by_kind_and_platform()
    {
        var manifest = UpdateManifest.Parse(FeedJson);

        Assert.Equal("1.0.2", manifest.Version);
        var asset = manifest.SelectAsset("cli", "windows-x64");
        Assert.NotNull(asset);
        Assert.Equal("https://example.com/Autarx-cli-windows-x64.zip", asset!.Url);
        Assert.Equal("abc123", asset.Sha256);
        Assert.Null(manifest.SelectAsset("gui", "linux-x64"));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.2", true)]
    [InlineData("1.0.2", "1.0.1", false)]
    [InlineData("1.0.1", "1.0.1", false)]
    [InlineData("v1.0.1", "1.2.0", true)]
    [InlineData("1.0.1", "2.0.0", true)]
    public void Compares_versions_semantically(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, UpdateFeed.IsNewer(current, candidate));
    }

    [Fact]
    public async Task Loads_feed_from_local_file_and_checks_against_it()
    {
        var feedPath = Path.Combine(_dir, "latest.json");
        await File.WriteAllTextAsync(feedPath, FeedJson);

        var check = await UpdateService.CheckAsync(feedPath, "1.0.1", "cli", "windows-x64");

        Assert.True(check.UpdateAvailable);
        Assert.Equal("1.0.2", check.LatestVersion);
        Assert.Equal("abc123", check.Asset!.Sha256);

        var upToDate = await UpdateService.CheckAsync(feedPath, "1.0.2", "cli", "windows-x64");
        Assert.False(upToDate.UpdateAvailable);
    }

    [Fact]
    public void Extract_verified_accepts_matching_hash_and_locates_payload_root()
    {
        var zipPath = BuildPackageZip(("autarx.exe", "new-binary"), ("config.json", "{}"));

        var root = UpdateInstaller.ExtractVerified(zipPath, Sha256Of(zipPath), "autarx.exe");

        Assert.True(File.Exists(Path.Combine(root, "autarx.exe")));
        Assert.Equal("new-binary", File.ReadAllText(Path.Combine(root, "autarx.exe")));
    }

    [Fact]
    public void Extract_verified_rejects_a_tampered_package_before_touching_anything()
    {
        var zipPath = BuildPackageZip(("autarx.exe", "new-binary"));

        Assert.Throws<UpdateException>(() =>
            UpdateInstaller.ExtractVerified(zipPath, new string('0', 64), "autarx.exe"));
    }

    [Fact]
    public void Apply_swaps_files_keeps_backups_and_cleans_stale_ones()
    {
        var zipPath = BuildPackageZip(("autarx.exe", "new-binary"), ("config.json", "{}"));
        var root = UpdateInstaller.ExtractVerified(zipPath, Sha256Of(zipPath), "autarx.exe");

        var appDir = Path.Combine(_dir, "app");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "autarx.exe"), "old-binary");
        File.WriteAllText(Path.Combine(appDir, "stale.autarx-update.old"), "leftover");

        var replaced = UpdateInstaller.Apply(root, appDir);

        Assert.Equal(2, replaced);
        Assert.Equal("new-binary", File.ReadAllText(Path.Combine(appDir, "autarx.exe")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(appDir, "config.json")));
        Assert.Equal("old-binary", File.ReadAllText(Path.Combine(appDir, "autarx.exe.autarx-update.old")));
        Assert.False(File.Exists(Path.Combine(appDir, "stale.autarx-update.old")));
    }

    private string BuildPackageZip(params (string Name, string Content)[] files)
    {
        var src = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(src, "cli");
        Directory.CreateDirectory(payload);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(payload, name), content);
        var zipPath = Path.Combine(_dir, Path.GetFileNameWithoutExtension(src) + ".zip");
        ZipFile.CreateFromDirectory(src, zipPath);
        return zipPath;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
