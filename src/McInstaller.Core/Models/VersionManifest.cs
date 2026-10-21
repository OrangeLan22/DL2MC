using System.Text.Json.Serialization;

namespace McInstaller.Core.Models;

/// <summary>
/// 版本清单（version_manifest_v2.json）。
/// </summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions Latest { get; set; } = new();

    [JsonPropertyName("versions")]
    public List<VersionSummary> Versions { get; set; } = [];
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string Release { get; set; } = "";

    [JsonPropertyName("snapshot")]
    public string Snapshot { get; set; } = "";
}

public sealed class VersionSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>release | snapshot | old_beta | old_alpha</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>版本详情 JSON 的 URL。</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>版本详情 JSON 的 SHA1，可用于校验。</summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset ReleaseTime { get; set; }
}
