using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Progress;

namespace McInstaller.App.ViewModels;

/// <summary>进度窗口里的一个任务</summary>
public partial class ProgressTaskItem(string name) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>该任务自身的完成度</summary>
    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _status = "等待中";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private bool _isFailed;

    /// <summary>该任务内已下载/已处理文件数说明</summary>
    [ObservableProperty]
    private string _detail = "";

    /// <summary>尚未开始：三个状态位都不为真</summary>
    public bool IsPending => !IsActive && !IsDone && !IsFailed;

    /// <summary>
    /// 下载已完成但整体仍未结束（例如 Forge installer 正在子进程里执行）
    /// 此时进度条改为不确定态
    /// </summary>
    public bool IsIndeterminate => IsActive && Percent >= 100;

    public string Glyph => IsFailed ? "✗" : IsDone ? "✓" : IsActive ? "●" : "○";

    partial void OnIsDoneChanged(bool value) => RaiseStateChanged();

    partial void OnIsFailedChanged(bool value) => RaiseStateChanged();

    partial void OnIsActiveChanged(bool value) => RaiseStateChanged();

    partial void OnPercentChanged(double value) => OnPropertyChanged(nameof(IsIndeterminate));

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsIndeterminate));
    }
}

/// <summary>
/// 下载/安装进度窗口的 ViewModel。
/// </summary>
public partial class ProgressWindowViewModel : ObservableObject, IDownloadProgress
{
    private readonly object _lock = new();
    private readonly Action? _onCancelRequested;
    private readonly Action? _onMinimizeRequested;

    // 当前任务内的计数
    private int _completed;
    private int _total;
    private int _failed;
    private int _skipped;
    private long _bytes;
    private long _totalBytes;

    // 速度采样
    private long _lastSampleBytes;
    private DateTime _lastSampleTime = DateTime.UtcNow;

    private int _currentIndex = -1;

    public ProgressWindowViewModel(
        string title, Action? onCancelRequested = null, Action? onMinimizeRequested = null)
    {
        Title = title;
        _onCancelRequested = onCancelRequested;
        _onMinimizeRequested = onMinimizeRequested;
        Tasks = [];
    }

    public string Title { get; }

    public ObservableCollection<ProgressTaskItem> Tasks { get; }

    /// <summary>顶层总进度（0~100）</summary>
    [ObservableProperty]
    private double _overallPercent;

    /// <summary>总进度百分比文本</summary>
    [ObservableProperty]
    private string _overallPercentText = "0%";

    /// <summary>实时下载速度</summary>
    [ObservableProperty]
    private string _speedText = "";

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _currentFile = "";

    [ObservableProperty]
    private bool _isFinished;

    [ObservableProperty]
    private string _summaryText = "";

    // ── 任务编排 ──────────────────────────────────────

    /// <summary>预先列出本次要执行的全部任务</summary>
    public void PlanTasks(IEnumerable<string> names)
    {
        // 同步执行：本方法由 UI 线程调用，用 Post 会让「建列表」与后续
        // 的 StartTask / OnFileCompleted 产生时序竞争，导致进度写到一个还不存在的任务上
        Tasks.Clear();
        _currentIndex = -1;

        foreach (var name in names)
        {
            Tasks.Add(new ProgressTaskItem(name));
        }

        RefreshOverall();
    }

    /// <summary>开始执行第 index 个任务，重置该任务的内部计数</summary>
    public void StartTask(int index)
    {
        lock (_lock)
        {
            _completed = 0;
            _total = 0;
            _failed = 0;
            _skipped = 0;
            _bytes = 0;
            _totalBytes = 0;
            _lastSampleBytes = 0;
            _lastSampleTime = DateTime.UtcNow;
        }

        _currentIndex = index;

        if (index < 0 || index >= Tasks.Count)
        {
            return;
        }

        var task = Tasks[index];
        task.IsActive = true;
        task.IsDone = false;
        task.IsFailed = false;
        task.Percent = 0;
        task.Status = "进行中";
        task.Detail = "";

        CurrentFile = "";
        SpeedText = "";
        ProgressText = BuildOverallProgressText();
        RefreshOverall();
    }

    /// <summary>结束第 index 个任务</summary>
    public void CompleteTask(int index, bool success)
    {
        if (index < 0 || index >= Tasks.Count)
        {
            return;
        }

        var task = Tasks[index];
        task.IsActive = false;
        task.IsDone = true;
        task.IsFailed = !success;
        task.Percent = success ? 100 : task.Percent;
        task.Status = success ? "完成" : "失败";

        if (success)
        {
            task.Detail = "已完成";
        }

        ProgressText = BuildOverallProgressText();
        RefreshOverall();
    }

    // ── IDownloadProgress ─────────────────────────────

    public void OnPlanned(int totalFiles, long totalBytes)
    {
        lock (_lock)
        {
            _total += totalFiles;
            _totalBytes += totalBytes;
        }

        // 立刻刷新
        Dispatcher.UIThread.Post(() =>
        {
            ProgressText = BuildOverallProgressText();
            RefreshOverall();
        });
    }

    public void OnFileCompleted(DownloadJob job, bool skipped, long bytes)
    {
        string? speed = null;
        double taskPercent = 0;
        string detail = "";

        lock (_lock)
        {
            _completed++;
            _bytes += bytes;

            if (skipped)
            {
                _skipped++;
            }

            if (_bytes > _totalBytes)
            {
                _totalBytes = _bytes;
            }

            taskPercent = _total == 0 ? 0 : Math.Min(100, (double)_completed / _total * 100);
            detail = $"{_completed}/{_total} 个文件";

            // 速度：每 0.4 秒采样一次
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSampleTime).TotalSeconds;
            if (elapsed >= 0.4)
            {
                var delta = _bytes - _lastSampleBytes;
                var bps = delta / elapsed;
                _lastSampleBytes = _bytes;
                _lastSampleTime = now;
                speed = FormatSpeed(bps);
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_currentIndex >= 0 && _currentIndex < Tasks.Count)
            {
                var task = Tasks[_currentIndex];
                task.Percent = taskPercent;
                task.Detail = detail;
            }

            CurrentFile = job.DisplayName;

            if (speed is not null)
            {
                SpeedText = speed;
            }

            ProgressText = BuildOverallProgressText();
            RefreshOverall();
        });
    }

    public void OnFileFailed(DownloadJob job, Exception error)
    {
        lock (_lock)
        {
            _completed++;
            _failed++;
        }

        Dispatcher.UIThread.Post(RefreshOverall);
    }

    // ── 汇总 ──────────────────────────────────────────

    /// <summary>
    /// 总进度下方文字
    /// </summary>
    private string BuildOverallProgressText()
    {
        var done = Tasks.Count(t => t.IsDone && !t.IsFailed);
        var failed = Tasks.Count(t => t.IsFailed);
        var total = Tasks.Count;

        var text = $"{done}/{total} 项完成";

        if (failed > 0)
        {
            text += $"   {failed} 项失败";
        }

        return text;
    }
    /// <summary>总进度 = 各任务进度的平均（未开始的任务按 0 计）</summary>
    private void RefreshOverall()
    {
        if (Tasks.Count == 0)
        {
            OverallPercent = 0;
            OverallPercentText = "0%";
            return;
        }

        // 注意 IsIndeterminate 的任务：它的 Percent 已经是 100（下载完了），
        // 但 installer 还在子进程里跑，实际并没结束
        // 若照搬 100，总进度会提前冲到 100%，与"还在加载"的视觉自相矛盾
        var sum = Tasks.Sum(t =>
        {
            if (t.IsDone && !t.IsFailed)
            {
                return 100.0;   // 真正结束
            }

            if (t.IsActive && t.IsIndeterminate)
            {
                return 99.0;    // 下载完成、安装进行中：封顶 99，不给满分
            }

            return t.Percent;
        });

        var raw = Math.Clamp(sum / Tasks.Count, 0, 100);
        var allDone = Tasks.All(t => t.IsDone);

        var displayed = allDone ? 100.0 : Math.Min(99.0, Math.Floor(raw));

        OverallPercent = displayed;
        OverallPercentText = $"{displayed:F0}%";
    }

    public void Finish(bool success, string summary)
    {
        IsFinished = true;
        SummaryText = summary;

        if (success)
        {
            OverallPercent = 100;
            OverallPercentText = "100%";
        }

        SpeedText = "";
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):F1} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:F0} KB/s",
        _ => $"{bytesPerSecond:F0} B/s",
    };

    [RelayCommand]
    private void Cancel() => _onCancelRequested?.Invoke();

    /// <summary>最小化到右下角状态栏</summary>
    [RelayCommand]
    private void Minimize() => _onMinimizeRequested?.Invoke();
}
