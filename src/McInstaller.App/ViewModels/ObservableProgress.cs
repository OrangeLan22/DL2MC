using McInstaller.Core.Pipeline;
using McInstaller.Core.Progress;

namespace McInstaller.App.ViewModels;

public sealed record ProgressSnapshot(
    int Completed,
    int Total,
    long Bytes,
    long TotalBytes,
    int Failed,
    int Skipped,
    string? CurrentFile)
{
    public double Percent => Total == 0 ? 0 : Math.Min(100, (double)Completed / Total * 100);

    public string BytesDisplay =>
        $"{FormatBytes(Bytes)} / {FormatBytes(TotalBytes)}";

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>
/// 把 Core 的多线程进度回调桥接到 UI。
/// </summary>
public sealed class ObservableProgress(Action<ProgressSnapshot> report) : IDownloadProgress
{
    private readonly object _lock = new();
    private int _completed;
    private int _total;
    private int _failed;
    private int _skipped;
    private long _bytes;
    private long _totalBytes;
    private string? _currentFile;
    private DateTime _lastReport = DateTime.MinValue;

    public void OnPlanned(int totalFiles, long totalBytes)
    {
        lock (_lock)
        {
            _total += totalFiles;
            _totalBytes += totalBytes;
        }

        Flush(force: true);
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
            _currentFile = job.DisplayName;

            if (_bytes > _totalBytes)
            {
                _totalBytes = _bytes;
            }
        }

        Flush();
    }

    public void OnFileFailed(DownloadJob job, Exception error)
    {
        lock (_lock)
        {
            _failed++;
            _completed++;
            _currentFile = job.DisplayName;
        }

        Flush();
    }

    private void Flush(bool force = false)
    {
        ProgressSnapshot snapshot;

        lock (_lock)
        {
            if (!force && (DateTime.UtcNow - _lastReport).TotalMilliseconds < 120)
            {
                return;
            }

            _lastReport = DateTime.UtcNow;

            snapshot = new ProgressSnapshot(
                _completed, _total, _bytes, _totalBytes, _failed, _skipped, _currentFile);
        }

        report(snapshot);
    }
}
