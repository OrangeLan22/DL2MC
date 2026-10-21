using McInstaller.Core.Models;
using McInstaller.Core.Util;

namespace McInstaller.Core.Sources;

/// <summary>
/// Mojang 官方源。所有 URL 均来自 JSON 自身字段，不做任何拼接猜测。
/// </summary>
public sealed class MojangSource : IDownloadSource
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string AssetObjectBase = "https://resources.download.minecraft.net/";
    private const string LibraryBase = "https://libraries.minecraft.net/";

    public string Name => "Mojang";

    public Uri GetVersionManifestUri() => new(ManifestUrl);

    public Uri GetVersionDetailUri(VersionSummary summary) => new(summary.Url);

    public Uri? GetClientJarUri(string versionId, DownloadArtifact? client)
        => client?.Url is { Length: > 0 } url ? new Uri(url) : null;

    public Uri? GetAssetIndexUri(string versionId, AssetIndexRef? index)
        => index?.Url is { Length: > 0 } url ? new Uri(url) : null;

    public Uri GetAssetObjectUri(string hash)
        => new($"{AssetObjectBase}{hash[..2]}/{hash}");

    public Uri GetLibraryUri(Library library, DownloadArtifact artifact, string relativePath)
    {
        // library 自带的仓库地址（Fabric 等第三方加载器）
        if (library.Url is { Length: > 0 } repo)
        {
            return new Uri($"{repo.TrimEnd('/')}/{relativePath}");
        }

        // artifact 给出的完整 url（26.x 的原版 libraries）
        if (artifact.Url is { Length: > 0 } artifactUrl)
        {
            return new Uri(artifactUrl);
        }

        return new Uri($"{LibraryBase}{relativePath}");
    }
}

/// <summary>
/// BMCLAPI 镜像源。仅作为可选加速，官方源必须始终保留在回退链末端。
/// 端点形态见 docs/ENDPOINTS.md §2。
/// </summary>
public sealed class BmclapiSource : IDownloadSource
{
    private const string Base = "https://bmclapi2.bangbang93.com";

    public string Name => "BMCLAPI";

    public Uri GetVersionManifestUri() => new($"{Base}/mc/game/version_manifest_v2.json");

    public Uri GetVersionDetailUri(VersionSummary summary) => new($"{Base}/version/{Uri.EscapeDataString(summary.Id)}/json");

    public Uri? GetClientJarUri(string versionId, DownloadArtifact? client)
        => new($"{Base}/version/{Uri.EscapeDataString(versionId)}/client");

    /// <summary>
    /// BMCLAPI 没有独立的资源索引镜像端点（实测未找到），
    /// 因此回退到 JSON 中给出的官方 url。索引文件只有几百 KB，不值得再引入第三方。
    /// </summary>
    public Uri? GetAssetIndexUri(string versionId, AssetIndexRef? index)
        => index?.Url is { Length: > 0 } url ? new Uri(url) : null;

    public Uri GetAssetObjectUri(string hash) => new($"{Base}/assets/{hash[..2]}/{hash}");

    public Uri GetLibraryUri(Library library, DownloadArtifact artifact, string relativePath)
    {
        // library 自带仓库（如 maven.fabricmc.net）没有镜像，必须走原地址
        if (library.Url is { Length: > 0 } repo)
        {
            return new Uri($"{repo.TrimEnd('/')}/{relativePath}");
        }

        return new Uri($"{Base}/maven/{relativePath}");
    }
}
