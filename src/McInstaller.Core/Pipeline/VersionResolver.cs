using McInstaller.Core.Models;
using McInstaller.Core.Net;
using McInstaller.Core.Sources;

namespace McInstaller.Core.Pipeline;

/// <summary>
/// 版本解析：版本清单 → 指定版本的详情 JSON。
/// </summary>
public sealed class VersionResolver(HttpFetcher http, IDownloadSource source)
{
    private VersionManifest? _cachedManifest;

    public IDownloadSource Source => source;

    /// <summary>获取版本清单（带缓存，一次进程内只拉一次）。</summary>
    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct = default)
    {
        if (_cachedManifest is not null)
        {
            return _cachedManifest;
        }

        var uri = source.GetVersionManifestUri();
        var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
        _cachedManifest = JsonDefaults.Deserialize<VersionManifest>(json);
        return _cachedManifest;
    }

    /// <summary>按 id 在清单中定位版本。找不到返回 null。</summary>
    public async Task<VersionSummary?> FindVersionAsync(string versionId, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
        return manifest.Versions.FirstOrDefault(
            v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 拉取某个版本的详情 JSON。
    /// 注意：返回的是原始 JSON 文本，因为落盘到 versions/&lt;id&gt;/&lt;id&gt;.json 时
    /// 应当保留原文（启动器需要其中完整信息，裁剪会出问题）。
    /// </summary>
    public async Task<(VersionDetail Detail, string RawJson)> GetDetailAsync(
        VersionSummary summary, CancellationToken ct = default)
    {
        var uri = source.GetVersionDetailUri(summary);
        var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
        var detail = JsonDefaults.Deserialize<VersionDetail>(json);
        return (detail, json);
    }
}
