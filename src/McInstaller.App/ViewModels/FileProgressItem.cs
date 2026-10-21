using CommunityToolkit.Mvvm.ComponentModel;
using McInstaller.Core.Pipeline;

namespace McInstaller.App.ViewModels;

public enum FileState
{
    Pending,
    Done,
    Skipped,
    Failed,
}

/// <summary>进度窗口文件列表中的一项</summary>
public partial class FileProgressItem(DownloadJob job) : ObservableObject
{
    public string DisplayName { get; } = job.DisplayName;

    public string RelativePath { get; } = job.RelativePath;

    public long? Size { get; } = job.Size;

    [ObservableProperty]
    private FileState _state = FileState.Pending;

    [ObservableProperty]
    private string _error = "";

    public string SizeText => Size is { } s ? ProgressSnapshot.FormatBytes(s) : "";

    /// <summary>左侧状态符号</summary>
    public string StateGlyph => State switch
    {
        FileState.Done => "✓",
        FileState.Skipped => "≡",
        FileState.Failed => "✗",
        _ => "·",
    };

    /// <summary>状态颜色键</summary>
    public string StateClass => State switch
    {
        FileState.Done => "done",
        FileState.Skipped => "skipped",
        FileState.Failed => "failed",
        _ => "pending",
    };

    partial void OnStateChanged(FileState value)
    {
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateClass));
    }
}
