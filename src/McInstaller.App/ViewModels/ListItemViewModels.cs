using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using McInstaller.Core.Models;
using McInstaller.Core.VersionManagement;

namespace McInstaller.App.ViewModels;

/// <summary>版本列表中的一项</summary>
public partial class VersionItemViewModel(VersionSummary summary, bool isInstalled) : ObservableObject
{
    public string Id { get; } = summary.Id;

    public string Type { get; } = summary.Type;

    public DateTimeOffset ReleaseTime { get; } = summary.ReleaseTime;

    public VersionSummary Summary { get; } = summary;

    /// <summary>本地 versions 下是否已有该版本</summary>
    [ObservableProperty]
    private bool _isInstalled = isInstalled;

    public bool IsRelease => Type == "release";

    public bool IsSnapshot => Type == "snapshot";

    public bool IsOld => Type is "old_beta" or "old_alpha";

    public string TypeDisplay => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };

    public string ReleaseTimeDisplay => ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd");

    public string InstalledMark => IsInstalled ? "已安装" : "";

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(InstalledMark));
}

/// <summary>下拉里的一个版本选项。Value 为 null 表示「自动」</summary>
public sealed record LoaderVersionOption(string Display, string? Value)
{
    public static LoaderVersionOption Auto(string label = "自动（最新适配）") => new(label, null);
}

/// <summary>已安装版本列表中的一项</summary>
public partial class InstalledVersionViewModel(InstalledVersion version) : ObservableObject
{
    [ObservableProperty]
    private InstalledVersion _version = version;

    public string Id => Version.Id;

    public string LoaderDisplay => Version.LoaderDisplay;

    public bool IsLoaderVersion => Version.IsLoaderVersion;

    public string ParentDisplay => string.IsNullOrEmpty(Version.InheritsFrom)
        ? "—"
        : Version.InheritsFrom;

    public string TypeDisplay => Version.Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        "unknown" => "无法识别",
        _ => Version.Type,
    };

    public string SizeDisplay => Version.JarSizeBytes > 0
        ? ProgressSnapshot.FormatBytes(Version.JarSizeBytes)
        : "无客户端 jar";

    partial void OnVersionChanged(InstalledVersion value)
    {
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(LoaderDisplay));
        OnPropertyChanged(nameof(IsLoaderVersion));
        OnPropertyChanged(nameof(ParentDisplay));
        OnPropertyChanged(nameof(TypeDisplay));
        OnPropertyChanged(nameof(SizeDisplay));
    }
}

/// <summary>可选的第三方加载器</summary>
public partial class LoaderOptionViewModel(string kind, string displayName, string description) : ObservableObject
{
    /// <summary>fabric / forge / neoforge / optifine</summary>
    public string Kind { get; } = kind;

    public string DisplayName { get; } = displayName;

    public string Description { get; } = description;

    /// <summary>是否需要可用的 Java 运行 installer</summary>
    public bool RequiresJava => Kind is "forge" or "neoforge";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>该版本下是否存在适配的加载器版本</summary>
    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>与已选中的其他加载器冲突</summary>
    [ObservableProperty]
    private bool _hasConflict;

    /// <summary>版本相关的提示（N 个可选 / 该版本暂无 OptiFine）</summary>
    [ObservableProperty]
    private string _versionNote = "";

    /// <summary>冲突提示</summary>
    [ObservableProperty]
    private string _conflictNote = "";

    /// <summary>最终是否可勾选：有适配版本 且 不与已选加载器冲突</summary>
    public bool IsSelectable => IsAvailable && !HasConflict;

    /// <summary>界面显示的提示：冲突优先于版本信息</summary>
    public string NoteText => HasConflict ? ConflictNote : VersionNote;

    /// <summary>该游戏版本下可选的加载器版本，首项固定为「自动」</summary>
    public ObservableCollection<LoaderVersionOption> Versions { get; } = [];

    [ObservableProperty]
    private LoaderVersionOption? _selectedVersion;

    [ObservableProperty]
    private bool _isLoadingVersions;

    /// <summary>用户显式指定的版本；null 表示自动选最新适配版本</summary>
    public string? RequestedVersion => SelectedVersion?.Value;

    partial void OnIsAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSelectable));

        if (!value)
        {
            IsSelected = false;
        }
    }

    partial void OnHasConflictChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSelectable));
        OnPropertyChanged(nameof(NoteText));

        // 冲突出现时取消勾选，避免带着非法组合去安装
        if (value)
        {
            IsSelected = false;
        }
    }

    partial void OnConflictNoteChanged(string value) => OnPropertyChanged(nameof(NoteText));

    partial void OnVersionNoteChanged(string value) => OnPropertyChanged(nameof(NoteText));
}
