using System.Xml.Linq;
using McInstaller.Core.Net;

namespace McInstaller.Core.Loaders;

/// <summary>
/// 基于 maven + 官方 installer 的加载器数据源。
/// Forge 与 NeoForge 的安装流程完全同构，差异只在版本号规则与仓库地址。
/// </summary>
public interface IMavenLoaderSource
{
    string LoaderType { get; }

    /// <summary>该 MC 版本可用的加载器版本，按新到旧排序。</summary>
    Task<IReadOnlyList<string>> GetLoaderVersionsAsync(string gameVersion, CancellationToken ct = default);

    /// <summary>把 MC 版本 + 加载器版本拼成 maven 里的完整版本号。</summary>
    string ToFullVersion(string gameVersion, string loaderVersion);

    /// <summary>installer jar 地址。</summary>
    Uri GetInstallerUri(string fullVersion);
}

internal static class MavenMetadata
{
    /// <summary>读取 maven-metadata.xml 中的版本列表。</summary>
    public static async Task<IReadOnlyList<string>> GetVersionsAsync(
        HttpFetcher http, string metadataUrl, CancellationToken ct)
    {
        var xml = await http.GetStringAsync(new Uri(metadataUrl), ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);

        return doc.Descendants("version")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();
    }

    /// <summary>按点分数字比较版本，避免 51.0.10 排到 51.0.9 前面。</summary>
    public sealed class VersionLikeComparer : IComparer<string>
    {
        public static readonly VersionLikeComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return string.CompareOrdinal(x, y);
            }

            var xs = x.Split('.', '-', '+');
            var ys = y.Split('.', '-', '+');

            for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
            {
                var xNumeric = int.TryParse(xs[i], out var xn);
                var yNumeric = int.TryParse(ys[i], out var yn);

                if (xNumeric && yNumeric)
                {
                    if (xn != yn)
                    {
                        return xn.CompareTo(yn);
                    }
                }
                else
                {
                    var cmp = string.CompareOrdinal(xs[i], ys[i]);
                    if (cmp != 0)
                    {
                        return cmp;
                    }
                }
            }

            return xs.Length.CompareTo(ys.Length);
        }
    }
}

/// <summary>
/// Forge 源。
/// 版本号格式 <c>&lt;mcVersion&gt;-&lt;forgeVersion&gt;</c>，用「MC 版本 + 连字符」做前缀匹配，
/// 不按固定段数切分（MC 版本自身可能含连字符，如 1.21-pre1）。
///
/// useMirror = true 时改走 BMCLAPI：maven.minecraftforge.net 在国内经常连不上，
/// 镜像提供同等的版本列表与 installer 下载。
/// </summary>
public sealed class ForgeMavenSource(HttpFetcher http, bool useMirror = false) : IMavenLoaderSource
{
    private const string Base = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string MirrorBase = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// 镜像的 Forge 版本 → build 号。
    /// 老版本（如 1.8.9）在镜像 maven 路径下没有 installer jar，
    /// 只能用 /forge/download/&lt;build&gt; 取。
    /// </summary>
    private readonly Dictionary<string, long> _mirrorBuilds = new(StringComparer.OrdinalIgnoreCase);

    public string LoaderType => "Forge";

    public async Task<IReadOnlyList<string>> GetLoaderVersionsAsync(
        string gameVersion, CancellationToken ct = default)
    {
        if (useMirror)
        {
            return await GetVersionsFromMirrorAsync(gameVersion, ct).ConfigureAwait(false);
        }

        var all = await MavenMetadata.GetVersionsAsync(http, $"{Base}/maven-metadata.xml", ct)
            .ConfigureAwait(false);

        var prefix = gameVersion + "-";

        return all
            .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(v => v[prefix.Length..])
            .OrderByDescending(v => v, MavenMetadata.VersionLikeComparer.Instance)
            .ToList();
    }

    /// <summary>通过 BMCLAPI 获取该 MC 版本的 Forge 列表。</summary>
    private async Task<IReadOnlyList<string>> GetVersionsFromMirrorAsync(
        string gameVersion, CancellationToken ct)
    {
        var json = await http
            .GetStringAsync(new Uri($"{MirrorBase}/forge/minecraft/{Uri.EscapeDataString(gameVersion)}"), ct)
            .ConfigureAwait(false);

        var entries = JsonDefaults.Deserialize<List<ForgeMirrorEntry>>(json);

        // 记录每个版本对应的 build 号，下载时用它拼 /forge/download/<build>
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Version) && entry.Build > 0)
            {
                _mirrorBuilds[entry.Version] = entry.Build;
            }
        }

        return entries
            .Select(e => e.Version)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .OrderByDescending(v => v, MavenMetadata.VersionLikeComparer.Instance)
            .ToList();
    }

    public string ToFullVersion(string gameVersion, string loaderVersion)
        => $"{gameVersion}-{loaderVersion}";

    public Uri GetInstallerUri(string fullVersion)
    {
        if (useMirror)
        {
            // fullVersion 形如 1.8.9-11.15.1.2318，后半段才是 Forge 版本
            var dash = fullVersion.IndexOf('-', StringComparison.Ordinal);
            var forgeVersion = dash > 0 ? fullVersion[(dash + 1)..] : fullVersion;

            if (_mirrorBuilds.TryGetValue(forgeVersion, out var build) && build > 0)
            {
                // 老版本在镜像 maven 路径下没有 installer，这个端点才是可靠的
                return new Uri($"{MirrorBase}/forge/download/{build}");
            }

            return new Uri(
                $"{MirrorBase}/maven/net/minecraftforge/forge/{fullVersion}/forge-{fullVersion}-installer.jar");
        }

        return new Uri($"{Base}/{fullVersion}/forge-{fullVersion}-installer.jar");
    }

    /// <summary>BMCLAPI /forge/minecraft/&lt;mc&gt; 的条目。</summary>
    private sealed class ForgeMirrorEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("mcversion")]
        public string McVersion { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("build")]
        public long Build { get; set; }
    }
}

/// <summary>
/// NeoForge 源。
///
/// 版本号规则与 Forge 不同（实测）：
///   MC 1.21.11 → NeoForge 21.11.x   （去掉 "1." 前缀）
///   MC 26.3    → NeoForge 26.3.x    （年份制版本号直接用）
///   MC 1.20.1 及更早 → 走 net.neoforged:forge，格式同 Forge
///
/// useMirror = true 时改走 BMCLAPI：maven.neoforged.net 在国内经常连不上
/// （实测连接超时 20 秒），镜像提供同等的版本列表与 installer 下载。
/// </summary>
public sealed class NeoForgeMavenSource(HttpFetcher http, bool useMirror = false) : IMavenLoaderSource
{
    private const string Base = "https://maven.neoforged.net/releases/net/neoforged";
    private const string MirrorBase = "https://bmclapi2.bangbang93.com";

    public string LoaderType => "NeoForge";

    public async Task<IReadOnlyList<string>> GetLoaderVersionsAsync(
        string gameVersion, CancellationToken ct = default)
    {
        if (useMirror)
        {
            return await GetVersionsFromMirrorAsync(gameVersion, ct).ConfigureAwait(false);
        }

        var legacy = UseLegacyForgeArtifact(gameVersion);

        if (legacy)
        {
            // 1.20.1 及更早：net.neoforged:forge，版本形如 1.20.1-47.1.106
            var all = await MavenMetadata.GetVersionsAsync(
                http, $"{Base}/forge/maven-metadata.xml", ct).ConfigureAwait(false);

            var prefix = gameVersion + "-";

            return all
                .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(v => v[prefix.Length..])
                .OrderByDescending(v => v, MavenMetadata.VersionLikeComparer.Instance)
                .ToList();
        }

        var versions = await MavenMetadata.GetVersionsAsync(
            http, $"{Base}/neoforge/maven-metadata.xml", ct).ConfigureAwait(false);

        var versionPrefix = ToVersionPrefix(gameVersion);

        return versions
            .Where(v => v.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase))
            // 排除带 -beta / -alpha 之类后缀的构建
            .Where(v => !v.Contains('-', StringComparison.Ordinal))
            .OrderByDescending(v => v, MavenMetadata.VersionLikeComparer.Instance)
            .ToList();
    }

    /// <summary>通过 BMCLAPI 获取该 MC 版本的 NeoForge 列表。</summary>
    private async Task<IReadOnlyList<string>> GetVersionsFromMirrorAsync(
        string gameVersion, CancellationToken ct)
    {
        var json = await http
            .GetStringAsync(new Uri($"{MirrorBase}/neoforge/list/{Uri.EscapeDataString(gameVersion)}"), ct)
            .ConfigureAwait(false);

        var entries = JsonDefaults.Deserialize<List<NeoForgeMirrorEntry>>(json);

        return entries
            .Select(e => e.Version)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            // 镜像会带上 beta 构建，默认排除
            .Where(v => !v.Contains('-', StringComparison.Ordinal))
            .OrderByDescending(v => v, MavenMetadata.VersionLikeComparer.Instance)
            .ToList();
    }

    public string ToFullVersion(string gameVersion, string loaderVersion)
        => UseLegacyForgeArtifact(gameVersion) ? $"{gameVersion}-{loaderVersion}" : loaderVersion;

    public Uri GetInstallerUri(string fullVersion)
    {
        if (useMirror && !fullVersion.Contains('-', StringComparison.Ordinal))
        {
            return new Uri(
                $"{MirrorBase}/maven/net/neoforged/neoforge/{fullVersion}/neoforge-{fullVersion}-installer.jar");
        }

        // 无法从版本号自身判断走哪个 artifact，由调用方传入的 fullVersion 决定：
        // 含连字符视为 1.20.1 时代的老格式
        var legacy = fullVersion.Contains('-', StringComparison.Ordinal);

        return legacy
            ? new Uri($"{Base}/forge/{fullVersion}/forge-{fullVersion}-installer.jar")
            : new Uri($"{Base}/neoforge/{fullVersion}/neoforge-{fullVersion}-installer.jar");
    }

    /// <summary>BMCLAPI /neoforge/list 的条目。</summary>
    private sealed class NeoForgeMirrorEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("mcversion")]
        public string McVersion { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("installerPath")]
        public string InstallerPath { get; set; } = "";
    }

    /// <summary>1.20.1 及更早的 NeoForge 仍发布在 net.neoforged:forge 下。</summary>
    private static bool UseLegacyForgeArtifact(string gameVersion)
        => string.Equals(gameVersion, "1.20.1", StringComparison.OrdinalIgnoreCase)
           || gameVersion.StartsWith("1.19", StringComparison.OrdinalIgnoreCase)
           || gameVersion.StartsWith("1.18", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// MC 版本 → NeoForge 版本前缀。
    /// 1.21.11 → "21.11."；26.3 → "26.3."
    /// </summary>
    internal static string ToVersionPrefix(string gameVersion)
        => gameVersion.StartsWith("1.", StringComparison.Ordinal)
            ? gameVersion[2..] + "."
            : gameVersion + ".";
}
