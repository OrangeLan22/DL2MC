using McInstaller.Core.Net;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Progress;

namespace McInstaller.Core.Mods;

public sealed record ModInstallResult(
    string Slug,
    string Title,
    string ModVersion,
    string FileName,
    string TargetPath,
    bool Success,
    bool Skipped,
    long Bytes,
    bool UsedVersionIsolation,
    string? ResolvedVersionId);

/// <summary>
/// 从 Modrinth 安装普通 mod（放 mods/ 目录）。
///
/// 注意 Fabric API 属于 mod 而非加载器：它的安装位置是 mods/，
/// 与 versions/ 下的加载器版本毫无关系。两者在 UI 上容易被混为一谈，代码路径必须分开。
/// </summary>
public sealed class ModInstaller(HttpFetcher http)
{
    private readonly ModrinthClient _modrinth = new(http);
    private readonly ConcurrentDownloader _downloader = new(http, concurrency: 2);

    public async Task<ModInstallResult> InstallAsync(
        string slug,
        string gameVersion,
        string loader,
        string minecraftDirectory,
        IDownloadProgress? progress = null,
        string? modVersionId = null,
        string? targetVersionId = null,
        CancellationToken ct = default)
    {
        var (title, actualSlug) = await _modrinth.GetProjectAsync(slug, ct).ConfigureAwait(false);
        var versions = await _modrinth.GetVersionsAsync(actualSlug, gameVersion, loader, ct).ConfigureAwait(false);

        if (versions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Modrinth 上没有 {actualSlug} 适配 Minecraft {gameVersion} + {loader} 的版本。" +
                "该项目可能尚未支持此游戏版本。");
        }

        var selected = modVersionId is null
            ? versions[0]
            : versions.FirstOrDefault(v => v.VersionNumber == modVersionId || v.Id == modVersionId)
              ?? throw new InvalidOperationException(
                  $"找不到版本 {modVersionId}；可用版本：" +
                  string.Join(", ", versions.Take(10).Select(v => v.VersionNumber)));

        var file = selected.PrimaryFile
            ?? throw new InvalidOperationException($"版本 {selected.VersionNumber} 没有文件");

        var mcDir = Path.GetFullPath(minecraftDirectory);
        var (modsRelative, usedIsolation, resolvedVersion) = ResolveModsDirectory(
            mcDir, gameVersion, loader, targetVersionId);

        var job = new DownloadJob(
            $"{modsRelative}/{file.Filename}",
            new Uri(file.Url),
            file.Sha1,
            file.Size,
            file.Filename);

        var result = await _downloader
            .RunAsync([job], mcDir, progress, ct)
            .ConfigureAwait(false);

        return new ModInstallResult(
            Slug: actualSlug,
            Title: title,
            ModVersion: selected.VersionNumber,
            FileName: file.Filename,
            TargetPath: Path.Combine(mcDir, modsRelative.Replace('/', Path.DirectorySeparatorChar), file.Filename),
            Success: result.Success,
            Skipped: result.Skipped > 0,
            Bytes: result.BytesDownloaded,
            UsedVersionIsolation: usedIsolation,
            ResolvedVersionId: resolvedVersion);
    }

    /// <summary>
    /// 定位 mods 目录。
    ///
    /// 版本隔离开启时，此时 mods 位于 versions/&lt;版本 id&gt;/mods/，
    /// 直接放 .minecraft/mods/ 会被启动器完全忽略（表现为“装了但列表里没有”）。
    ///
    /// 优先级：显式指定 &gt; 自动匹配隔离版本 &gt; 根 mods/。
    /// </summary>
    private static (string RelativePath, bool Isolated, string? VersionId) ResolveModsDirectory(
        string mcDir, string gameVersion, string loader, string? targetVersionId)
    {
        if (!string.IsNullOrWhiteSpace(targetVersionId))
        {
            return ($"versions/{targetVersionId}/mods", true, targetVersionId);
        }

        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir))
        {
            return ("mods", false, null);
        }

        var candidates = Directory.EnumerateDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Where(n => n.Contains(gameVersion, StringComparison.OrdinalIgnoreCase))
            .Where(n => MatchesLoader(n, loader))
            .ToList();

        return candidates.Count switch
        {
            1 => ($"versions/{candidates[0]}/mods", true, candidates[0]),
            0 => ("mods", false, null),
            _ => throw new InvalidOperationException(
                $"找到多个可能的隔离版本目录：{string.Join(", ", candidates)}。\n" +
                "请用 --target 显式指定目标版本（mod 必须装到将被启动的那个版本目录下）。"),
        };
    }

    private static bool MatchesLoader(string directoryName, string loader)
    {
        var name = directoryName.ToLowerInvariant();
        var kind = loader.ToLowerInvariant();

        // neoforge 的名称也包含 "forge"，必须先判 neoforge
        return kind switch
        {
            "fabric" => name.Contains("fabric"),
            "neoforge" => name.Contains("neoforge"),
            "forge" => name.Contains("forge") && !name.Contains("neoforge"),
            _ => name.Contains(kind),
        };
    }
}
