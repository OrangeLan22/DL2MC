using System.Diagnostics;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Progress;

namespace McInstaller.Cli;

/// <summary>
/// 控制台进度显示。回调来自多个下载线程，内部加锁并节流。
/// 进度条写在 stderr，不干扰 stdout 的结构化结果。
/// 标签刻意使用 ASCII —— 进度条会被反复重绘，混入多字节字符在部分终端会出现残影。
/// </summary>
public sealed class ConsoleProgress : IDownloadProgress
{
    private readonly object _lock = new();
    private readonly Stopwatch _sinceRender = Stopwatch.StartNew();
    private readonly TimeSpan _renderInterval = TimeSpan.FromMilliseconds(150);

    private int _total;
    private long _totalBytes;
    private int _completed;
    private int _skipped;
    private int _failed;
    private long _bytes;

    public void OnPlanned(int totalFiles, long totalBytes)
    {
        // 累加而非重置：一次安装分成 core / assets 两次规划，总量应当相加
        lock (_lock)
        {
            _total += totalFiles;
            _totalBytes += totalBytes;
        }

        Render(force: true);
    }

    public void OnFileCompleted(DownloadJob job, bool skipped, long bytes)
    {
        lock (_lock)
        {
            _completed++;
            if (skipped)
            {
                _skipped++;
            }

            _bytes += bytes;

            // 部分来源（Fabric 的 intermediary / fabric-loader）不提供 size 字段，
            // 规划出的总量会偏小。实际下载量超过时向上修正，避免出现 >100% 的进度。
            if (_bytes > _totalBytes)
            {
                _totalBytes = _bytes;
            }
        }

        Render();
    }

    public void OnFileFailed(DownloadJob job, Exception error)
    {
        lock (_lock)
        {
            _failed++;
            _completed++;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  [FAIL] {job.DisplayName}: {error.Message}");
        Render();
    }

    public void Finish()
    {
        Render(force: true);
        Console.Error.WriteLine();
    }

    private void Render(bool force = false)
    {
        lock (_lock)
        {
            if (!force && _sinceRender.Elapsed < _renderInterval)
            {
                return;
            }

            _sinceRender.Restart();

            var percent = _total == 0 ? 0 : (double)_completed / _total * 100;
            const int width = 28;
            var filled = Math.Clamp((int)(percent / 100 * width), 0, width);
            var bar = new string('#', filled) + new string('-', width - filled);

            var line =
                $"\r  [{bar}] {_completed,6}/{_total}  {percent,5:F1}%  " +
                $"{FormatBytes(_bytes),10} / {FormatBytes(_totalBytes),10}" +
                (_skipped > 0 ? $"  skip:{_skipped}" : "") +
                (_failed > 0 ? $"  fail:{_failed}" : "");

            Console.Error.Write(line);
        }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}
