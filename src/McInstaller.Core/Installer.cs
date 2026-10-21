using McInstaller.Core.Models;
using McInstaller.Core.Net;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Progress;
using McInstaller.Core.Sources;

namespace McInstaller.Core;

public sealed record InstallRequest(string VersionId, string MinecraftDirectory);

public sealed record InstallResult(
    bool Success,
    string VersionId,
    string? VersionJsonPath,
    int FilesDownloaded,
    int FilesSkipped,
    int FilesFailed,
    long BytesDownloaded,
    IReadOnlyList<DownloadFailure> Failures);

/// <summary>
/// 安装编排：解析版本 → 规划任务 → 下载 → 落盘版本 JSON。
///
/// 安装顺序刻意分成三段：
///   1. client jar + libraries（约 100 MB）
///   2. 写 versions/&lt;id&gt;/&lt;id&gt;.json —— 此时 启动器已能在列表中看到该版本
///   3. assets（最大的一块，1.21.11 约 462 MB）
///
/// 这样用户能较快看到版本出现，而不是盯着进度条等完 500 MB。
/// 但只有全部文件就位才算 Success —— 中途失败会如实报告。
/// </summary>
public sealed class Installer
{
    private readonly IDownloadSource _source;
    private readonly IDownloadSource? _mirror;
    private readonly VersionResolver _resolver;
    private readonly LibraryResolver _libraries;
    private readonly AssetPlanner _assets;
    private readonly ConcurrentDownloader _downloader;

    /// <param name="mirror">
    /// 备用镜像源。传入后默认仍走官方源，只有实测速度低于阈值（或下载失败）
    /// 才自动切到镜像，避免在官方源本来就健康的网络环境下强制走第三方。
    /// </param>
    /// <param name="slowThresholdBytesPerSecond">
    /// 「下载慢」的判定阈值（字节/秒）。
    /// 0 表示不启用自动切源，固定使用主源。
    /// </param>
    public Installer(
        HttpFetcher http,
        IDownloadSource source,
        IDownloadSource? mirror = null,
        PlatformInfo? platform = null,
        int concurrency = 24,
        long slowThresholdBytesPerSecond = 0,
        Action<string>? onNotice = null)
    {
        _source = source;
        _mirror = mirror;
        _resolver = new VersionResolver(http, source);
        _libraries = new LibraryResolver(platform ?? PlatformInfo.Current);
        _assets = new AssetPlanner(http);
        _downloader = new ConcurrentDownloader(
            http, concurrency, slowThresholdBytesPerSecond, onNotice);
    }

    /// <summary>本次安装是否曾因官方源太慢而切到镜像。</summary>
    public bool SwitchedToMirror => _downloader.SwitchedToMirror;

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IDownloadProgress? progress = null,
        CancellationToken ct = default)
    {
        progress ??= NullProgress.Instance;

        var summary = await _resolver.FindVersionAsync(request.VersionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"版本清单中不存在：{request.VersionId}");

        var (detail, rawJson) = await _resolver.GetDetailAsync(summary, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(detail.InheritsFrom))
        {
            throw new NotSupportedException(
                $"版本 {detail.Id} 带 inheritsFrom={detail.InheritsFrom}，P0 阶段尚未实现继承合并。" +
                "请先安装其父版本，或使用 §8 的加载器安装流程。");
        }

        var mcDir = Path.GetFullPath(request.MinecraftDirectory);

        // ── 一次性规划全部任务 ──
        // 关键在于「先算总数、再开始下」：分多段下载会让进度总量反复变化，
        // 表现为前一阶段跑满 100% 又掉回去（即用户看到的「进度条来回跳」）。
        var allJobs = new List<DownloadJob>();
        AddClientJar(allJobs, detail);
        allJobs.AddRange(_libraries.Resolve(detail, _source, _mirror));

        AssetPlan? assetPlan = null;

        if (detail.AssetIndex is { } assetIndex)
        {
            assetPlan = await _assets.PlanAsync(detail.Id, assetIndex, _source, _mirror, ct)
                .ConfigureAwait(false);

            allJobs.AddRange(assetPlan.Jobs);
        }

        // ── 一次性下载 ──
        var download = await _downloader.RunAsync(allJobs, mcDir, progress, ct).ConfigureAwait(false);

        // ── 全部就位后再落元数据 ──
        string? versionJsonPath = null;

        if (download.Success)
        {
            versionJsonPath = await WriteVersionJsonAsync(mcDir, detail.Id, rawJson, ct).ConfigureAwait(false);

            // 索引文件本身必须落盘：启动器要靠 assets/indexes/<id>.json 验证资源
            if (assetPlan is not null)
            {
                await WriteAssetIndexAsync(mcDir, assetPlan.IndexId, assetPlan.RawJson, ct).ConfigureAwait(false);
            }
        }

        return new InstallResult(
            Success: download.Success,
            VersionId: detail.Id,
            VersionJsonPath: versionJsonPath,
            FilesDownloaded: download.Downloaded,
            FilesSkipped: download.Skipped,
            FilesFailed: download.Failed,
            BytesDownloaded: download.BytesDownloaded,
            Failures: download.Failures);
    }

    private void AddClientJar(List<DownloadJob> jobs, VersionDetail detail)
    {
        var client = detail.Downloads?.Client;
        var uri = _source.GetClientJarUri(detail.Id, client);

        if (uri is null)
        {
            return;
        }

        // 备用源：官方 piston-data 慢时换 BMCLAPI 的 /version/<id>/client
        Func<Uri>? fallback = _mirror?.GetClientJarUri(detail.Id, client) is { } mirrorUri
            ? () => mirrorUri
            : null;

        // 目录名与文件名都必须等于 id，否则启动器会把版本归入「不常用版本」
        jobs.Add(new DownloadJob(
            $"versions/{detail.Id}/{detail.Id}.jar",
            uri,
            client?.Sha1,
            client?.Size,
            "client.jar",
            fallback));
    }

    /// <summary>
    /// 落盘资源索引。路径固定为 assets/indexes/&lt;indexId&gt;.json，
    /// 文件名必须是 indexId 而非版本号。
    /// </summary>
    private static async Task WriteAssetIndexAsync(
        string mcDir, string indexId, string rawJson, CancellationToken ct)
    {
        var indexDir = Path.Combine(mcDir, "assets", "indexes");
        Directory.CreateDirectory(indexDir);

        var path = Path.Combine(indexDir, $"{indexId}.json");
        var tempPath = path + ".tmp";

        await File.WriteAllTextAsync(tempPath, rawJson, ct).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// 落盘版本 JSON。
    /// 必须写原始 JSON 文本 —— 启动器需要其中完整的 libraries / assetIndex / arguments，
    /// 序列化我们自己的模型会丢掉未建模的字段。
    /// </summary>
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
