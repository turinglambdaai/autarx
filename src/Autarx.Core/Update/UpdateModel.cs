using System.Text.Json;

namespace Autarx.Core.Update;

public sealed record UpdateAsset(
    string Url,
    string Sha256,
    long SizeBytes);

/// <summary>
/// The machine-readable update feed (latest.json, published as a release
/// asset by the release pipeline). Assets are keyed [kind][platform], e.g.
/// assets["cli"]["windows-x64"] — the same names the release pipeline uses.
/// </summary>
public sealed record UpdateManifest(
    string Version,
    string? Date,
    string? NotesUrl,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, UpdateAsset>> Assets)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static UpdateManifest Parse(string json) =>
        JsonSerializer.Deserialize<UpdateManifest>(json, Options)
        ?? throw new UpdateException("update feed is empty");

    public UpdateAsset? SelectAsset(string kind, string platform)
    {
        if (!Assets.TryGetValue(kind, out var platforms))
            return null;
        return platforms.GetValueOrDefault(platform);
    }
}

/// <summary>Anything that goes wrong during an update check or apply — the
/// updater reports it as an operational failure, never a crash.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }

    public UpdateException(string message, Exception inner) : base(message, inner) { }
}
