using McInstaller.Core.Models;
using McInstaller.Core.Net;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Progress;
using McInstaller.Core.Sources;

namespace McInstaller.Core.Loaders;

/// <summary>
/// Fabric 安装器 —— 五个目标里唯一「纯文件操作」的一个：
/// 不需要 Java、不需要子进程、不需要自研安装逻辑。
///
/// 产出极简：一个约 4.8 KB 的版本 JSON + 8 个库文件。
/// 不复制 client jar、不合并父版本 JSON —— 已实测：启动器支持 inheritsFrom 继承链。
///
/// 两个已实测的坑（见 docs/DESIGN.md §8.3）：
///   1. library 自带的 url 必须优先（8/8 个库指向 maven.fabricmc.net，写死默认仓库会整体失败）
///   2. sha1/size 挂在 library 层级而非 downloads.artifact，不回退就会失去校验
/// </summary>
public sealed class FabricInstaller : ILoaderInstaller
{
    private readonly IDownloadSource _source;
    private readonly FabricMetaClient _meta;
    private readonly LibraryResolver _libraries;
    private readonly ConcurrentDownloader _downloader;

    public FabricInstaller(HttpFetcher http, IDownloadSource source, PlatformInfo? platform = null, int concurrency = 24)
    {
        _source = source;
        _meta = new FabricMetaClient(http);
        _libraries = new LibraryResolver(platform ?? PlatformInfo.Current);
        _downloader = new ConcurrentDownloader(http, concurrency);
    }

    public string Name => "Fabric";

    public async Task<LoaderInstallResult> InstallAsync(
        string gameVersion,
        string? loaderVersion,
        string minecraftDirectory,
        IDownloadProgress? progress = null,
        CancellationToken ct = default)
    {
        progress ??= NullProgress.Instance;

        var mcDir = Path.GetFullPath(minecraftDirectory);
        EnsureParentInstalled(mcDir, gameVersion);

        var loaders = await _meta.GetLoaderVersionsAsync(ct).ConfigureAwait(false);
        var selected = SelectLoader(loaders, loaderVersion);

        var (detail, rawJson) = await _meta.GetProfileAsync(gameVersion, selected.Version, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(detail.Id))
        {
            throw new InvalidOperationException("Fabric profile 缺少 id 字段，无法确定版本目录名");
        }

        // 下载 Fabric 自己的库（8 个，几 MB）
        var jobs = _libraries.Resolve(detail, _source);
        var downloadResult = await _downloader.RunAsync(jobs, mcDir, progress, ct).ConfigureAwait(false);

        // 库就位后才落 JSON —— 避免失败时留下「能被识别但启动不了」的残留版本
        var jsonPath = await WriteVersionJsonAsync(mcDir, detail.Id, rawJson, ct).ConfigureAwait(false);

        return new LoaderInstallResult(
            LoaderType: Name,
            VersionId: detail.Id,
            ParentVersion: gameVersion,
            LoaderVersion: selected.Version,
            Success: downloadResult.Success,
            VersionJsonPath: jsonPath,
            FilesDownloaded: downloadResult.Downloaded,
            FilesSkipped: downloadResult.Skipped,
            FilesFailed: downloadResult.Failed,
            BytesDownloaded: downloadResult.BytesDownloaded,
            Failures: downloadResult.Failures);
    }

    /// <summary>
    /// 父版本是硬依赖：Fabric 版本没有自己的 client jar，
    /// 全靠 inheritsFrom 指向的父版本。缺失时必须在开始前就报错。
    /// </summary>
    private static void EnsureParentInstalled(string mcDir, string gameVersion)
    {
        var parentJson = Path.Combine(mcDir, "versions", gameVersion, $"{gameVersion}.json");

        if (!File.Exists(parentJson))
        {
            throw new InvalidOperationException(
                $"父版本 {gameVersion} 未安装（找不到 {parentJson}）。" +
                $"请先执行：install {gameVersion} --dir \"{mcDir}\"");
        }
    }

    private static FabricLoaderVersion SelectLoader(
        IReadOnlyList<FabricLoaderVersion> loaders, string? requested)
    {
        if (loaders.Count == 0)
        {
            throw new InvalidOperationException("Fabric loader 列表为空");
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            return loaders.FirstOrDefault(l => l.Version == requested)
                ?? new FabricLoaderVersion { Version = requested, Stable = false };
        }

        // 没有显式指定时优先 stable；实测常见情况是最新版本非 stable 而 stable 仍往前一个
        return loaders.FirstOrDefault(l => l.Stable) ?? loaders[0];
    }

    private static async Task<string> WriteVersionJsonAsync(
        string mcDir, string versionId, string rawJson, CancellationToken ct)
    {
        var versionDir = Path.Combine(mcDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        var path = Path.Combine(versionDir, $"{versionId}.json");
        var tempPath = path + ".tmp";

        await File.WriteAllTextAsync(tempPath, rawJson, ct).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);

        return path;
    }
}
