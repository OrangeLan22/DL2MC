using System.Text.Json.Serialization;
using McInstaller.Core.Net;
using McInstaller.Core.Models;

namespace McInstaller.Core.Loaders;

/// <summary>Fabric loader 版本条目（来自 meta.fabricmc.net/v2/versions/loader）。</summary>
public sealed class FabricLoaderVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }

    [JsonPropertyName("maven")]
    public string Maven { get; set; } = "";
}

/// <summary>
/// Fabric Meta API 封装。
/// 端点实测见 docs/ENDPOINTS.md §3。
/// </summary>
public sealed class FabricMetaClient(HttpFetcher http)
{
    private const string Base = "https://meta.fabricmc.net";

    /// <summary>
    /// 获取全部 loader 版本。
    /// 实测 255 个条目，但只有 1 个 stable —— 新版 loader 大量以非 stable 发布，
    /// 因此「取最新 stable」与「取最新」是两种不同策略，应允许用户覆盖。
    /// </summary>
    public async Task<IReadOnlyList<FabricLoaderVersion>> GetLoaderVersionsAsync(CancellationToken ct = default)
    {
        var json = await http.GetStringAsync(new Uri($"{Base}/v2/versions/loader"), ct).ConfigureAwait(false);
        return JsonDefaults.Deserialize<List<FabricLoaderVersion>>(json);
    }

    /// <summary>该游戏版本下的 loader 条目（外层包装）。</summary>
    private sealed class LoaderEntry
    {
        [JsonPropertyName("loader")]
        public FabricLoaderVersion Loader { get; set; } = new();
    }

    /// <summary>
    /// 列出某个游戏版本可用的 loader 版本（从新到旧）。
    ///
    /// 端点 /v2/versions/loader/&lt;game&gt; 的返回结构与 /v2/versions/loader 不同：
    /// 每项是 { loader, intermediary, launcherMeta } 的包装对象，loader 版本在 loader 子对象里。
    /// </summary>
    public async Task<IReadOnlyList<FabricLoaderVersion>> GetLoadersForGameAsync(
        string gameVersion, CancellationToken ct = default)
    {
        var uri = new Uri($"{Base}/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}");
        var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);

        var entries = JsonDefaults.Deserialize<List<LoaderEntry>>(json);

        return entries
            .Select(e => e.Loader)
            .Where(l => !string.IsNullOrWhiteSpace(l.Version))
            .ToList();
    }

    /// <summary>
    /// 取某个游戏版本 + loader 版本的完整 profile。
    ///
    /// 返回的 JSON 自带 mainClass / arguments / libraries 与 inheritsFrom，
    /// 结构天然满足启动器对继承子版本的最低字段要求（实测可直接启动）。
    /// libraries 只列出 Fabric 自己的 8 个库，其余全部继承自父版本。
    /// </summary>
    public async Task<(VersionDetail Detail, string RawJson)> GetProfileAsync(
        string gameVersion, string loaderVersion, CancellationToken ct = default)
    {
        var uri = new Uri(
            $"{Base}/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json");

        var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
        var detail = JsonDefaults.Deserialize<VersionDetail>(json);
        return (detail, json);
    }
}
