using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using McInstaller.Core.Net;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Progress;

namespace McInstaller.Core.Pipeline;

public sealed record DownloadFailure(DownloadJob Job, string Error);

public sealed record DownloadSummary(
    int Total,
    int Downloaded,
    int Skipped,
    int Failed,
    long BytesDownloaded,
    IReadOnlyList<DownloadFailure> Failures,
    bool SwitchedToMirror = false)
{
    public bool Success => Failed == 0;
}

/// <summary>
/// 并发下载器。所有网络出口都汇聚到这里。
///
/// 三条硬性要求（见 docs/DESIGN.md §7.3）：
///   1. 原子落盘：先写 .tmp，校验通过后改名。中断不会留下「存在但错误」的文件
///   2. 跳过逻辑必须是「校验后跳过」而非「存在即跳过」
///   3. 进度回调节流，避免数千次回调打爆 UI
///
/// 另外负责**自适应切源**：默认走官方源，实测速度低于阈值时自动切到镜像，
/// 之后的所有文件都走镜像，避免每个文件都重新踩一遍慢速。
/// </summary>
public sealed class ConcurrentDownloader(
    HttpFetcher http,
    int concurrency = 24,
    long slowThresholdBytesPerSecond = 0,
    Action<string>? onNotice = null)
{
    /// <summary>累积下载量达到该值后才判断速度，避免小文件测速失真。</summary>
    private const long SpeedProbeBytes = 8L * 1024 * 1024;

    private readonly object _speedLock = new();
    private readonly Stopwatch _probeWatch = Stopwatch.StartNew();
    private long _probeBytes;
    private volatile bool _usingFallback;

    public bool SwitchedToMirror => _usingFallback;

    public async Task<DownloadSummary> RunAsync(
        IReadOnlyList<DownloadJob> jobs,
        string rootDirectory,
        IDownloadProgress? progress = null,
        CancellationToken ct = default)
    {
        progress ??= NullProgress.Instance;

        // 按落盘路径去重。
        // 版本 JSON 里同一个 jar 常被多条 library 引用 ——
        // 例如 1.16.5 的 org.lwjgl:lwjgl 同时以「仅 artifact」和
        // 「artifact + natives classifier」两种形态出现，路径完全相同。
        // 不去重的话两个并发任务会写同一个 .tmp，报「文件被另一进程占用」。
        jobs = jobs
            .GroupBy(j => j.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        Directory.CreateDirectory(rootDirectory);
        progress.OnPlanned(jobs.Count, jobs.Sum(j => j.Size ?? 0));

        // 把完整清单交给 UI，让它能事先列出所有待下载文件
        progress.OnJobsPlanned(jobs);

        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        long bytesDownloaded = 0;
        var failures = new ConcurrentBag<DownloadFailure>();

        using var gate = new SemaphoreSlim(concurrency);

        var tasks = jobs.Select(async job =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var target = Path.Combine(rootDirectory, job.RelativePath);

                if (await IsAlreadyValidAsync(target, job, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref skipped);
                    progress.OnFileCompleted(job, skipped: true, 0);
                    return;
                }

                var bytes = await DownloadOneAsync(job, target, ct).ConfigureAwait(false);
                Interlocked.Increment(ref downloaded);
                Interlocked.Add(ref bytesDownloaded, bytes);
                progress.OnFileCompleted(job, skipped: false, bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                failures.Add(new DownloadFailure(job, ex.Message));
                progress.OnFileFailed(job, ex);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new DownloadSummary(
            jobs.Count, downloaded, skipped, failed, bytesDownloaded, failures.ToList(), _usingFallback);
    }

    /// <summary>
    /// 校验已存在文件是否可直接复用。
    /// 先比大小（便宜），再算 SHA1（贵）。没有 SHA1 的条目只比大小。
    /// </summary>
    private static async Task<bool> IsAlreadyValidAsync(string path, DownloadJob job, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);

        if (job.Size is { } expectedSize && info.Length != expectedSize)
        {
            return false;
        }

        if (string.IsNullOrEmpty(job.Sha1))
        {
            return true;
        }

        var actual = await ComputeSha1Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual, job.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> DownloadOneAsync(DownloadJob job, string target, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = target + ".tmp";
        var useFallback = _usingFallback && job.HasFallback;

        // 清理上次中断留下的临时文件；若它正被别的进程占用（如杀软扫描），
        // 换一个唯一名字，避免整个任务直接失败。
        if (File.Exists(tempPath))
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                tempPath = $"{target}.{Guid.NewGuid():N}.tmp";
            }
            catch (UnauthorizedAccessException)
            {
                tempPath = $"{target}.{Guid.NewGuid():N}" + ".tmp";
            }
        }

        try
        {
            var bytes = await DownloadAndVerifyAsync(job, tempPath, useFallback, ct).ConfigureAwait(false);
            File.Move(tempPath, target, overwrite: true);
            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception) when (!useFallback && job.HasFallback)
        {
            // 主源失败（含超时/重试耗尽），还有备用源就再试一次
            TryDelete(tempPath);
            onNotice?.Invoke($"下载失败，改用镜像源重试：{job.DisplayName}");

            var bytes = await DownloadAndVerifyAsync(job, tempPath, useFallback: true, ct).ConfigureAwait(false);
            File.Move(tempPath, target, overwrite: true);
            return bytes;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private async Task<long> DownloadAndVerifyAsync(
        DownloadJob job, string tempPath, bool useFallback, CancellationToken ct)
    {
        var uri = useFallback && job.FallbackUriFactory is not null
            ? job.FallbackUriFactory()
            : job.Uri;

        var watch = Stopwatch.StartNew();
        var bytes = await http.DownloadToFileAsync(uri, tempPath, ct).ConfigureAwait(false);
        watch.Stop();

        if (!string.IsNullOrEmpty(job.Sha1))
        {
            var actual = await ComputeSha1Async(tempPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, job.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA1 校验失败：期望 {job.Sha1}，实际 {actual}");
            }
        }

        RecordSpeed(bytes, watch.Elapsed);
        return bytes;
    }

    /// <summary>
    /// 记录下载速度，并在累积足够样本后判断是否需要切到镜像。
    /// 只在第一次切换前统计，切换后不再重复判断。
    /// </summary>
    private void RecordSpeed(long bytes, TimeSpan elapsed)
    {
        if (_usingFallback || slowThresholdBytesPerSecond <= 0)
        {
            return;
        }

        lock (_speedLock)
        {
            if (_usingFallback)
            {
                return;
            }

            _probeBytes += bytes;

            var seconds = _probeWatch.Elapsed.TotalSeconds;
            if (_probeBytes < SpeedProbeBytes || seconds < 1)
            {
                return;
            }

            var bytesPerSecond = _probeBytes / seconds;
            if (bytesPerSecond >= slowThresholdBytesPerSecond)
            {
                return;
            }

            _usingFallback = true;

            onNotice?.Invoke(
                $"官方源速度 {FormatSpeed(bytesPerSecond)} 低于阈值 " +
                $"{FormatSpeed(slowThresholdBytesPerSecond)}，后续文件改用镜像源");
        }
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):F1} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:F0} KB/s",
        _ => $"{bytesPerSecond:F0} B/s",
    };

    private static async Task<string> ComputeSha1Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}
