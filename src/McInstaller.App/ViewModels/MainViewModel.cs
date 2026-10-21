using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using McInstaller.App.Views;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McInstaller.App.Services;
using McInstaller.Core;
using McInstaller.Core.Loaders;
using McInstaller.Core.Net;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Progress;
using McInstaller.Core.Sources;
using McInstaller.Core.VersionManagement;

namespace McInstaller.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ConfigService _configService = new();
    private readonly AppConfig _config;
    private HttpFetcher _http;
    private CancellationTokenSource? _cts;
    /// <summary>
    /// 加载器版本列表加载的取消源
    /// 避免竞态
    /// </summary>
    private CancellationTokenSource? _loaderLoadCts;
    private bool _initialized;

    public MainViewModel()
    {
        _config = _configService.Load();
        _http = CreateHttp();

        _minecraftDirectory = _config.MinecraftDirectory ?? "";
        _themeModeIndex = _config.ThemeMode;
        _mirrorEnabled = _config.MirrorEnabled;
        _autoSwitchMirror = _config.AutoSwitchMirror;
        _concurrency = _config.Concurrency;
        _maxRetries = _config.MaxRetries;
        _autoRefreshOnStartup = _config.AutoRefreshOnStartup;
        _openFolderAfterInstall = _config.OpenFolderAfterInstall;
        _networkLogEnabled = _config.NetworkLogEnabled;
        _checkUpdateOnStartup = _config.CheckUpdateOnStartup;
        _slowThresholdKBps = _config.SlowSpeedThresholdKBps;
        _showLogPanel = _config.ShowLogPanel;
        _logPanelHeightPx = _config.LogPanelHeightPx;
        _leftPanelWidthPx = _config.LeftPanelWidthPx;

        foreach (var dir in _config.KnownDirectories)
        {
            KnownDirectories.Add(dir);
        }

        ApplyTheme(_config.ThemeMode);

        Loaders =
        [
            new LoaderOptionViewModel("fabric", "Fabric", "轻量，加载快。不需要单独的 Java 进程"),
            new LoaderOptionViewModel("forge", "Forge", "老牌模组平台，安装时需运行官方 installer（需 Java）"),
            new LoaderOptionViewModel("neoforge", "NeoForge", "Forge 分支，现代模组常用（需 Java）"),
            new LoaderOptionViewModel(
                "optifine",
                "OptiFine",
                "光影与性能优化。数据仅通过 BMCLAPI 镜像提供，不受「镜像加速」开关影响"),
        ];

        // 任一加载器的勾选变化都要重算冲突
        foreach (var loader in Loaders)
        {
            loader.PropertyChanged += OnLoaderPropertyChanged;
        }
    }

    private void OnLoaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoaderOptionViewModel.IsSelected))
        {
            RecomputeConflicts();
        }
    }

    /// <summary>
    /// 重新计算加载器间冲突
    /// </summary>
    private void RecomputeConflicts()
    {
        var selectedKinds = Loaders.Where(l => l.IsSelected).Select(l => l.Kind).ToList();

        foreach (var loader in Loaders)
        {
            var conflicting = selectedKinds
                .Where(other => !string.Equals(other, loader.Kind, StringComparison.OrdinalIgnoreCase)
                                && LoaderConflicts.AreConflicting(loader.Kind, other))
                .Select(other => Loaders.FirstOrDefault(l => l.Kind == other)?.DisplayName ?? other)
                .ToList();

            loader.HasConflict = conflicting.Count > 0;
            loader.ConflictNote = conflicting.Count > 0
                ? $"与 {string.Join("、", conflicting)} 冲突"
                : "";
        }
    }

    // ── 绑定属性 ────

    [ObservableProperty]
    private string _minecraftDirectory;

    [ObservableProperty]
    private VersionItemViewModel? _selectedVersion;

    [ObservableProperty]
    private InstalledVersionViewModel? _selectedInstalled;

    /// <summary>右侧面板切换依据：正在管理已安装版本</summary>
    public bool HasSelectedInstalled => SelectedInstalled is not null;

    /// <summary>右侧面板切换依据：正在准备新增版本</summary>
    public bool HasSelectedVersion => SelectedVersion is not null;

    /// <summary>当前板块：0 = 已安装，1 = 添加新版本</summary>
    [ObservableProperty]
    private int _activeTab;

    public bool IsInstalledTab => ActiveTab == 0;

    public bool IsAddTab => ActiveTab == 1;

    /// <summary>已安装板块选中了具体项时才显示管理面板</summary>
    public bool ShowInstalledPanel => ActiveTab == 0 && SelectedInstalled is not null;

    /// <summary>新增板块选中了具体项时才显示安装面板</summary>
    public bool ShowAddPanel => ActiveTab == 1 && SelectedVersion is not null;

    public bool ShowPlaceholder => !ShowInstalledPanel && !ShowAddPanel;

    public string PlaceholderText => ActiveTab == 0
        ? "从左侧「已安装」列表中选择一个版本，可查看信息与重命名"
        : "从左侧「可下载」列表中选择一个游戏版本，配置加载器后开始安装";

    [RelayCommand]
    private void SwitchToInstalled() => ActiveTab = 0;

    [RelayCommand]
    private void SwitchToAdd() => ActiveTab = 1;

    partial void OnActiveTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsInstalledTab));
        OnPropertyChanged(nameof(IsAddTab));
        OnPropertyChanged(nameof(ShowInstalledPanel));
        OnPropertyChanged(nameof(ShowAddPanel));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(PlaceholderText));
        OnPropertyChanged(nameof(RefreshTooltip));

        // 切换板块时清空对侧的选择，右侧回到提示状态
        if (value == 0)
        {
            SelectedVersion = null;
        }
        else
        {
            SelectedInstalled = null;

            // 进入「添加新版本」不自动选中任何条目，避免右侧直接显示上一次的详情
            SelectedVersion = null;
        }
    }

    /// <summary>新增版本时的自定义名称</summary>
    [ObservableProperty]
    private string _newVersionName = "";

    /// <summary>名称校验错误（非空时安装按钮禁用）</summary>
    [ObservableProperty]
    private string? _newVersionNameError;

    /// <summary>重命名输入框的值</summary>
    [ObservableProperty]
    private string _renameInput = "";

    [ObservableProperty]
    private bool _isRenameMode;

    [ObservableProperty]
    private string? _renameError;

    /// <summary>更新确认弹窗是否可见</summary>
    [ObservableProperty]
    private bool _showConfirmDialog;

    /// <summary>下载进度覆盖层的数据源；为 null 表示不显示</summary>
    [ObservableProperty]
    private ProgressWindowViewModel? _downloadProgress;

    /// <summary>下载进度覆盖层是否可见（可独立控制，用于最小化）</summary>
    [ObservableProperty]
    private bool _showDownloadOverlay;

    /// <summary>覆盖层已最小化到右下角状态栏</summary>
    [ObservableProperty]
    private bool _isOverlayMinimized;

    /// <summary>安装仍在进行、且覆盖层处于最小化状态时可重新调出</summary>
    public bool CanRestoreOverlay => DownloadProgress is not null && IsOverlayMinimized;

    partial void OnDownloadProgressChanged(ProgressWindowViewModel? value)
        => OnPropertyChanged(nameof(CanRestoreOverlay));

    partial void OnIsOverlayMinimizedChanged(bool value)
        => OnPropertyChanged(nameof(CanRestoreOverlay));

    /// <summary>操作结果提示（安装/更新完成后弹出）</summary>
    [ObservableProperty]
    private bool _showResultDialog;

    [ObservableProperty]
    private string _resultTitle = "";

    [ObservableProperty]
    private string _resultMessage = "";

    /// <summary>结果是否为成功（决定图标与配色）</summary>
    [ObservableProperty]
    private bool _resultSuccess = true;

    /// <summary>是否显示「复制日志」按钮。业务性提示（如无法删除）不需要</summary>
    [ObservableProperty]
    private bool _resultOfferLog;

    /// <summary>加载器版本列表正在拉取中。此期间遮罩住加载器区，防止误操作</summary>
    [ObservableProperty]
    private bool _isLoadingLoaders;

    /// <summary>加载超过阈值后追加的提示（网络慢 / 建议开镜像）</summary>
    [ObservableProperty]
    private string _loadingSlowHint = "";

    /// <summary>加载器变更是否可应用：没在安装中、也没在拉取版本列表</summary>
    public bool CanApplyChanges => !IsBusy && !IsLoadingLoaders;

    [ObservableProperty]
    private string _confirmTitle = "";

    [ObservableProperty]
    private string _confirmMessage = "";

    [ObservableProperty]
    private string _confirmAcceptText = "确认执行";

    [ObservableProperty]
    private string _confirmCancelText = "取消";

    private TaskCompletionSource<bool>? _confirmTcs;
    private bool _checkingUpdate;

    /// <summary>弹出确认框并等待用户选择。防误触用</summary>
    private Task<bool> ShowConfirmAsync(string title, string message)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ShowConfirmDialog = true;
        _confirmTcs = new TaskCompletionSource<bool>();
        return _confirmTcs.Task;
    }

    [RelayCommand]
    private Task CheckUpdates() => CheckForUpdatesAsync(silent: false);

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_checkingUpdate)
        {
            return;
        }

        _checkingUpdate = true;

        try
        {
            var latest = await UpdateChecker.GetLatestAsync();
            var current = AppVersion;

            if (latest is null)
            {
                if (!silent)
                {
                    ShowResult(
                        "获取版本信息失败",
                        "无法连接 GitHub。可能是网络不通，或仓库还没有发布 Release。",
                        success: false);
                }

                return;
            }

            if (!UpdateChecker.IsNewer(latest.Tag, current))
            {
                if (!silent)
                {
                    ShowResult("已是最新版本", $"当前版本 {current}，没有发现更新的版本。", success: true);
                }

                return;
            }

            Log($"发现新版本：{latest.Tag}（当前 {current}）");

            // 设置界面常驻提示，即便不弹窗也能看到
            UpdateNotice = $"发现新版本 {latest.Tag}，点击查看";
            _pendingUpdateUrl = latest.HtmlUrl;

            var note = string.IsNullOrWhiteSpace(latest.Body)
                ? string.Empty
                : latest.Body.Trim() + "\n\n";

            ConfirmAcceptText = "前往下载";
            ConfirmCancelText = "稍后";
            var goDownload = await ShowConfirmAsync(
                $"发现新版本 {latest.Tag}",
                $"当前版本：{current}\n最新版本：{latest.Tag}\n\n{note}点击「前往下载」打开 Release 页面，手动下载新版本。");
            ConfirmAcceptText = "确认执行";
            ConfirmCancelText = "取消";

            if (goDownload)
            {
                OpenExternalUrl(latest.HtmlUrl);
            }
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    /// <summary>点击设置里的新版本提示，打开下载页</summary>
    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (!string.IsNullOrWhiteSpace(_pendingUpdateUrl))
        {
            OpenExternalUrl(_pendingUpdateUrl);
        }
    }
    private void OpenExternalUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            Log($"无法打开链接：{url}");
        }
    }

    partial void OnIsLoadingLoadersChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyChanges));
        OnPropertyChanged(nameof(LoaderLoadingText));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanApplyChanges));

    /// <summary>加载器区处于加载状态时显示的说明文字</summary>
    public string LoaderLoadingText => ActiveTab == 0
        ? "正在读取该版本可用的加载器版本…"
        : "正在读取各加载器的可用版本…";

    /// <summary>弹出操作结果提示</summary>
    /// <param name="offerLog">是否提供「复制日志」按钮——只有真正的错误才需要</param>
    private void ShowResult(string title, string message, bool success, bool offerLog = false)
    {
        ResultTitle = title;
        ResultMessage = message;
        ResultSuccess = success;
        ResultOfferLog = offerLog;
        ShowResultDialog = true;
    }

    /// <summary>复制当前日志全文到剪贴板</summary>
    [RelayCommand]
    private async Task CopyLogAsync()
    {
        if (Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return;
        }

        if (window.Clipboard is null || string.IsNullOrEmpty(LogText))
        {
            return;
        }

        try
        {
            await window.Clipboard.SetTextAsync(LogText);
            Log("日志已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Log($"复制日志失败：{ex.Message}");
        }
    }
    [RelayCommand]
    private void CloseResultDialog() => ShowResultDialog = false;
    [RelayCommand]
    private void ConfirmYes()
    {
        ShowConfirmDialog = false;
        _confirmTcs?.TrySetResult(true);
        _confirmTcs = null;
    }

    [RelayCommand]
    private void ConfirmNo()
    {
        ShowConfirmDialog = false;
        _confirmTcs?.TrySetResult(false);
        _confirmTcs = null;
    }

    [ObservableProperty]
    private bool _showReleases = true;

    [ObservableProperty]
    private bool _showSnapshots;

    [ObservableProperty]
    private bool _showOldVersions;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _mirrorEnabled = true;

    /// <summary>官方源慢时自动切镜像。关闭且镜像开启时直接走镜像</summary>
    [ObservableProperty]
    private bool _autoSwitchMirror = true;

    /// <summary>下载并发连接数</summary>
    [ObservableProperty]
    private int _concurrency = 24;

    /// <summary>单文件下载的最大重试次数</summary>
    [ObservableProperty]
    private int _maxRetries = 4;

    /// <summary>并发数的文本代理：输入框清空或非法时保留原值，避免绑定转换异常</summary>
    public string ConcurrencyText
    {
        get => Concurrency.ToString();
        set
        {
            if (int.TryParse(value, out var parsed) && parsed > 0)
            {
                Concurrency = parsed;
            }
        }
    }

    /// <summary>重试次数的文本代理</summary>
    public string MaxRetriesText
    {
        get => MaxRetries.ToString();
        set
        {
            if (int.TryParse(value, out var parsed) && parsed >= 0)
            {
                MaxRetries = parsed;
            }
        }
    }

    /// <summary>慢速阈值的文本代理</summary>
    public string SlowThresholdText
    {
        get => SlowThresholdKBps.ToString();
        set
        {
            if (int.TryParse(value, out var parsed) && parsed > 0)
            {
                SlowThresholdKBps = parsed;
            }
        }
    }

    /// <summary>启动时自动刷新可下载版本列表</summary>
    [ObservableProperty]
    private bool _autoRefreshOnStartup = true;

    /// <summary>安装完成后自动打开目标目录</summary>
    [ObservableProperty]
    private bool _openFolderAfterInstall;

    /// <summary>把每个 HTTP 请求写进运行日志</summary>
    [ObservableProperty]
    private bool _networkLogEnabled;

    /// <summary>启动时自动检查 GitHub 是否有新版本</summary>
    [ObservableProperty]
    private bool _checkUpdateOnStartup = true;

    /// <summary>设置界面里的新版本提示，空串表示没有</summary>
    [ObservableProperty]
    private string _updateNotice = "";

    /// <summary>待打开的下载页地址</summary>
    private string _pendingUpdateUrl = "";

    [ObservableProperty]
    private bool _showSettings;

    /// <summary>历史目录下拉是否展开</summary>
    [ObservableProperty]
    private bool _isDirectoryPopupOpen;

    /// <summary>「下载慢」阈值（KB/s）</summary>
    [ObservableProperty]
    private double _slowThresholdKBps;

    [ObservableProperty]
    private bool _showLogPanel = true;

    /// <summary>
    /// 日志面板高度
    /// </summary>
    [ObservableProperty]
    private double _logPanelHeightPx = 240;

    /// <summary>左侧列表宽度（像素）</summary>
    [ObservableProperty]
    private double _leftPanelWidthPx = 340;

    /// <summary>用过的目录历史</summary>
    public ObservableCollection<string> KnownDirectories { get; } = [];

    /// <summary>当前版本号</summary>
    public string AppVersion
    {
        get
        {
            var fromChangelog = ChangelogService.GetLatestVersion();

            if (!string.IsNullOrWhiteSpace(fromChangelog) &&
                !string.Equals(fromChangelog, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return fromChangelog;
            }

            // changelog 读不到时退回 csproj 里的 <Version>，避免版本比较全是「有更新」
            var version = typeof(MainViewModel).Assembly.GetName().Version;

            return version is null ? "unknown" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    [ObservableProperty]
    private int _themeModeIndex;

    /// <summary>主题单选：跟随系统</summary>
    public bool IsThemeSystem
    {
        get => ThemeModeIndex == 0;
        set { if (value) ThemeModeIndex = 0; }
    }

    /// <summary>主题单选：亮色</summary>
    public bool IsThemeLight
    {
        get => ThemeModeIndex == 1;
        set { if (value) ThemeModeIndex = 1; }
    }

    /// <summary>主题单选：暗色</summary>
    public bool IsThemeDark
    {
        get => ThemeModeIndex == 2;
        set { if (value) ThemeModeIndex = 2; }
    }

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _currentFileText = "";

    [ObservableProperty]
    private string _detailVersionId = "—";

    [ObservableProperty]
    private string _detailType = "—";

    [ObservableProperty]
    private string _detailReleaseTime = "—";

    [ObservableProperty]
    private string _detailJava = "—";

    [ObservableProperty]
    private string _detailSize = "—";

    [ObservableProperty]
    private string _detailStatus = "请从左侧选择版本";

    /// <summary>界面上显示的版本</summary>
    public ObservableCollection<VersionItemViewModel> Versions { get; } = [];

    /// <summary>未经搜索过滤的全量版本</summary>
    private readonly List<VersionItemViewModel> _allVersions = [];

    /// <summary>版本搜索框内容</summary>
    [ObservableProperty]
    private string _versionSearchText = "";

    /// <summary>过滤结果数量提示</summary>
    [ObservableProperty]
    private string _versionFilterHint = "";

    partial void OnVersionSearchTextChanged(string value) => ApplyVersionFilter();

    /// <summary>
    /// 把 <see cref="_allVersions"/> 按搜索词过滤进 <see cref="Versions"/>。
    /// </summary>
    private void ApplyVersionFilter()
    {
        var query = VersionSearchText?.Trim();

        Versions.Clear();

        foreach (var item in _allVersions)
        {
            if (string.IsNullOrEmpty(query)
                || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Versions.Add(item);
            }
        }

        VersionFilterHint = string.IsNullOrEmpty(query)
            ? ""
            : Versions.Count == 0
                ? "没有匹配的版本"
                : $"匹配 {Versions.Count} / {_allVersions.Count}";
    }

    /// <summary>本地已安装的版本</summary>
    public ObservableCollection<InstalledVersionViewModel> InstalledVersions { get; } = [];

    public ObservableCollection<LoaderOptionViewModel> Loaders { get; }

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>日志全文。用一个文本块整体渲染，才能跨行选中复制</summary>
    [ObservableProperty]
    private string _logText = "";

    // ── 初始化 ────────────────────────────────────────

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        Log($"配置文件：{ConfigService.ConfigPath}");

        if (!string.IsNullOrWhiteSpace(MinecraftDirectory))
        {
            // 已有配置目录时补进历史
            if (!KnownDirectories.Any(d => string.Equals(d, MinecraftDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                KnownDirectories.Add(MinecraftDirectory);
                _config.KnownDirectories.RemoveAll(
                    d => string.Equals(d, MinecraftDirectory, StringComparison.OrdinalIgnoreCase));
                _config.KnownDirectories.Insert(0, MinecraftDirectory);
                _configService.Save(_config);
            }

            Log($"已配置目录：{MinecraftDirectory}");
        }
        else
        {
            Log("尚未配置启动器目录，请先在顶部选择 .minecraft 路径");
        }

        await RefreshVersionsAsync();
        RefreshInstalled();

        if (_config.CheckUpdateOnStartup)
        {
            _ = CheckForUpdatesAsync(silent: true);
        }
    }

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在获取版本列表…";

        try
        {
            var resolver = new VersionResolver(_http, CurrentSource);
            var manifest = await resolver.GetManifestAsync();

            var installed = ScanInstalledVersions();

            _allVersions.Clear();

            foreach (var v in manifest.Versions)
            {
                var visible = v.Type switch
                {
                    "release" => ShowReleases,
                    "snapshot" => ShowSnapshots,
                    _ => ShowOldVersions,
                };

                if (!visible)
                {
                    continue;
                }

                _allVersions.Add(new VersionItemViewModel(v, installed.Contains(v.Id)));
            }

            // 让搜索框的当前输入立刻作用到新数据上
            ApplyVersionFilter();

            StatusText = $"共 {Versions.Count} 个版本（正式版 {Versions.Count(x => x.IsRelease)}，" +
                         $"快照 {Versions.Count(x => x.IsSnapshot)}，远古 {Versions.Count(x => x.IsOld)}）";
            Log($"版本列表已刷新：{Versions.Count} 项，本地已安装 {installed.Count} 个");

            // 刷新后不自动选中任何版本
            SelectedVersion = null;
        }
        catch (Exception ex)
        {
            StatusText = "获取版本列表失败";
            Log($"错误：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnShowReleasesChanged(bool value) => _ = RefreshVersionsAsync();

    partial void OnShowSnapshotsChanged(bool value) => _ = RefreshVersionsAsync();

    partial void OnShowOldVersionsChanged(bool value) => _ = RefreshVersionsAsync();

    partial void OnMinecraftDirectoryChanged(string value)
    {
        _config.MinecraftDirectory = value;

        if (!string.IsNullOrWhiteSpace(value))
        {
            RememberDirectory(value);
        }

        OnPropertyChanged(nameof(FooterDirectory));

        _configService.Save(_config);

        RefreshInstalled();
        ValidateNewVersionName();

        // 已安装标记重算
        if (Versions.Count > 0)
        {
            var installed = ScanInstalledVersions();
            foreach (var v in Versions)
            {
                v.IsInstalled = installed.Contains(v.Id);
            }

            UpdateDetailForSelection();
        }
    }

    partial void OnSelectedVersionChanged(VersionItemViewModel? value)
    {
        if (value is null)
        {
            OnPropertyChanged(nameof(HasSelectedVersion));
            OnPropertyChanged(nameof(ShowAddPanel));
            OnPropertyChanged(nameof(ShowPlaceholder));
            return;
        }

        OnPropertyChanged(nameof(HasSelectedVersion));
        OnPropertyChanged(nameof(ShowAddPanel));
        OnPropertyChanged(nameof(ShowPlaceholder));

        _config.LastVersionId = value.Id;
        _configService.Save(_config);

        UpdateDetailForSelection();
        _ = LoadVersionDetailAsync(value);
        _ = LoadLoaderVersionsAsync(value.Id);

        // 新增版本预填名称
        NewVersionName = LauncherLocator.Validate(MinecraftDirectory) is null
            ? VersionRenamer.MakeUniqueName(MinecraftDirectory, value.Id)
            : value.Id;
    }

    partial void OnMirrorEnabledChanged(bool value)
    {
        _config.MirrorEnabled = value;
        _configService.Save(_config);

        Log(value
            ? (AutoSwitchMirror ? "已启用镜像：官方源慢时自动切换" : "已启用镜像：直接使用镜像源")
            : "已关闭镜像，固定使用官方源");

        OnPropertyChanged(nameof(SourceDisplay));

        OnPropertyChanged(nameof(CanEditThreshold));
    }

    partial void OnConcurrencyChanged(int value)
    {
        _config.Concurrency = Math.Clamp(value, 1, 64);
        _configService.Save(_config);

        // 并发数
        _http.Dispose();
        _http = CreateHttp();
    }

    partial void OnMaxRetriesChanged(int value)
    {
        _config.MaxRetries = Math.Clamp(value, 0, 10);
        _configService.Save(_config);
    }

    partial void OnAutoRefreshOnStartupChanged(bool value)
    {
        _config.AutoRefreshOnStartup = value;
        _configService.Save(_config);
    }

    partial void OnOpenFolderAfterInstallChanged(bool value)
    {
        _config.OpenFolderAfterInstall = value;
        _configService.Save(_config);
    }
    partial void OnCheckUpdateOnStartupChanged(bool value)
    {
        _config.CheckUpdateOnStartup = value;
        _configService.Save(_config);
    }

    partial void OnNetworkLogEnabledChanged(bool value)
    {
        _config.NetworkLogEnabled = value;
        _configService.Save(_config);

        Log(value ? "已开启网络日志（每个 HTTP 请求都会记录）" : "已关闭网络日志");
    }

    partial void OnAutoSwitchMirrorChanged(bool value)
    {
        _config.AutoSwitchMirror = value;
        _configService.Save(_config);

        if (MirrorEnabled)
        {
            Log(value ? "镜像模式：官方源慢时自动切换" : "镜像模式：直接使用镜像源");
        }

        OnPropertyChanged(nameof(SourceDisplay));

        OnPropertyChanged(nameof(CanEditThreshold));
    }

    /// <summary>阈值输入框是否可编辑</summary>
    public bool CanEditThreshold => MirrorEnabled && AutoSwitchMirror;
    // ── 底部状态栏（footer）展示值 ──────────────────

    /// <summary>当前启动器目录</summary>
    public string FooterDirectory =>
        string.IsNullOrWhiteSpace(MinecraftDirectory) ? "未选择启动器目录" : MinecraftDirectory;

    public string ThemeDisplay => ThemeModeIndex switch
    {
        1 => "亮色",
        2 => "暗色",
        _ => "跟随系统",
    };

    /// <summary>下载源当前策略的简述</summary>
    public string SourceDisplay => !MirrorEnabled
        ? "官方源"
        : AutoSwitchMirror
            ? "自适应下载源"
            : "BMCLAPI 镜像";

    partial void OnSlowThresholdKBpsChanged(double value)
    {
        _config.SlowSpeedThresholdKBps = value;
        _configService.Save(_config);
    }

    partial void OnShowLogPanelChanged(bool value)
    {
        _config.ShowLogPanel = value;
        _configService.Save(_config);
    }

    /// <summary>拖动分隔条后调用</summary>
    public void SaveLayoutSize(double logHeightPx, double leftWidthPx)
    {
        LogPanelHeightPx = logHeightPx;
        LeftPanelWidthPx = leftWidthPx;

        _config.LogPanelHeightPx = logHeightPx;
        _config.LeftPanelWidthPx = leftWidthPx;
        _configService.Save(_config);
    }

    /// <summary>
    /// 记住用过的目录（去重、置顶）。
    /// </summary>
    private void RememberDirectory(string directory)
    {
        var existingIndex = _config.KnownDirectories.FindIndex(
            d => string.Equals(d, directory, StringComparison.OrdinalIgnoreCase));

        if (existingIndex == 0)
        {
            return;
        }

        if (existingIndex > 0)
        {
            _config.KnownDirectories.RemoveAt(existingIndex);
        }

        _config.KnownDirectories.Insert(0, directory);
        _config.KnownDirectories = _config.KnownDirectories.Take(10).ToList();

        KnownDirectories.Clear();
        foreach (var dir in _config.KnownDirectories)
        {
            KnownDirectories.Add(dir);
        }
    }

    [RelayCommand]
    private void OpenSettings() => ShowSettings = true;

    [RelayCommand]
    private void CloseSettings() => ShowSettings = false;

    /// <summary>从历史目录里移除一项</summary>
    [RelayCommand]
    private void RemoveDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        KnownDirectories.Remove(directory);
        _config.KnownDirectories.Remove(directory);

        // 删掉的是当前目录时，清空当前选中
        if (string.Equals(MinecraftDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            MinecraftDirectory = "";
        }

        _configService.Save(_config);
        Log($"已从历史目录移除：{directory}");
    }

    /// <summary>切换到历史里的某个目录</summary>
    [RelayCommand]
    private void SelectDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        MinecraftDirectory = directory;
    }

    partial void OnThemeModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(ThemeDisplay));
        ApplyTheme(value);
        _config.ThemeMode = value;
        _configService.Save(_config);
    }

    /// <summary>
    /// 切换主题
    /// </summary>
    private static void ApplyTheme(int mode)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = mode switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void UpdateDetailForSelection()
    {
        if (SelectedVersion is not { } v)
        {
            return;
        }

        DetailVersionId = v.Id;
        DetailType = v.TypeDisplay;
        DetailReleaseTime = v.ReleaseTimeDisplay;
        DetailStatus = v.IsInstalled ? "已安装（可直接启动）" : "未安装，将下载完整客户端";
    }

    private async Task LoadVersionDetailAsync(VersionItemViewModel item)
    {
        try
        {
            DetailJava = "读取中…";
            DetailSize = "读取中…";

            var resolver = new VersionResolver(_http, CurrentSource);
            var (detail, _) = await resolver.GetDetailAsync(item.Summary);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedVersion != item)
                {
                    return; // 用户切走
                }

                DetailJava = detail.JavaVersion is { MajorVersion: > 0 } jv
                    ? $"Java {jv.MajorVersion}+"
                    : "未声明";

                var clientBytes = detail.Downloads?.Client?.Size ?? 0;
                var assetsBytes = detail.AssetIndex?.TotalSize ?? 0;
                DetailSize = ProgressSnapshot.FormatBytes(clientBytes + assetsBytes);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DetailJava = "读取失败";
                DetailSize = "—";
            });
            Log($"读取版本详情失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 四个加载器在该游戏版本下可选的版本列表
    /// </summary>
    private async Task LoadLoaderVersionsAsync(
        string gameVersion, string? selectedKind = null, string? selectedVersion = null)
    {
        // 取消上一次的加载
        _loaderLoadCts?.Cancel();
        _loaderLoadCts?.Dispose();
        _loaderLoadCts = new CancellationTokenSource();
        var ct = _loaderLoadCts.Token;

        IsLoadingLoaders = true;
        LoadingSlowHint = "";

        // 5 秒给出可操作的建议
        using var slowHintCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ShowSlowLoadingHintAsync(slowHintCts.Token);

        try
        {
            foreach (var loader in Loaders)
            {
                loader.IsLoadingVersions = true;
                loader.VersionNote = "读取中…";
                loader.Versions.Clear();
                loader.Versions.Add(LoaderVersionOption.Auto());
                loader.SelectedVersion = loader.Versions[0];
                loader.IsSelected = false;
                loader.HasConflict = false;
                loader.ConflictNote = "";
            }

            await Task.WhenAll(Loaders.Select(loader => LoadOneLoaderAsync(loader, gameVersion, ct)));

            ct.ThrowIfCancellationRequested();

            // 版本列表到位后再同步当前状态
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 再确认
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                foreach (var loader in Loaders)
                {
                    if (selectedKind is null || loader.Kind != selectedKind || !loader.IsAvailable)
                    {
                        continue;
                    }

                    loader.IsSelected = true;

                    if (selectedVersion is not null)
                    {
                        var match = loader.Versions.FirstOrDefault(v => v.Value == selectedVersion);
                        if (match is not null)
                        {
                            loader.SelectedVersion = match;
                        }
                    }
                }

                RecomputeConflicts();
            });
        }
        catch (OperationCanceledException)
        {
            // 被新请求取代，静默退出
        }
        finally
        {
            // 只有「仍然是当前请求」时才复位加载状态
            if (!ct.IsCancellationRequested)
            {
                slowHintCts.Cancel();
                IsLoadingLoaders = false;
                LoadingSlowHint = "";
            }
        }
    }

    /// <summary>
    /// 加载超过 5 秒仍未完成时给出具体建议
    /// </summary>
    private async Task ShowSlowLoadingHintAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   
        }

        if (!IsLoadingLoaders)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            LoadingSlowHint = MirrorEnabled
                ? "网络不佳，或镜像源服务器繁忙，请稍候…"
                : "加载时间太长，可尝试在设置中开启镜像源";
        });
    }

    private async Task LoadOneLoaderAsync(
        LoaderOptionViewModel loader, string gameVersion, CancellationToken ct)
    {
        try
        {
            var versions = await FetchLoaderVersionsAsync(loader.Kind, gameVersion, ct);

            ct.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 丢弃本次结果
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                loader.Versions.Clear();
                loader.Versions.Add(LoaderVersionOption.Auto());

                foreach (var v in versions)
                {
                    loader.Versions.Add(new LoaderVersionOption(v, v));
                }

                loader.SelectedVersion = loader.Versions[0];
                loader.IsAvailable = versions.Count > 0;
                loader.IsLoadingVersions = false;

                loader.VersionNote = versions.Count switch
                {
                    0 => loader.Kind == "optifine" ? "该版本暂无 OptiFine" : "无适配版本",
                    _ => $"{versions.Count} 个可选",
                };
            });
        }
        catch (OperationCanceledException)
        {
            // 用户切走
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                loader.IsLoadingVersions = false;

                // 探测失败即禁止勾选
                loader.IsAvailable = false;
                loader.IsSelected = false;
                loader.VersionNote = "未能探测，暂不可选";
            });

            Log($"{loader.DisplayName} 版本列表获取失败：{ex.Message}");
        }
    }

    /// <summary>按加载器类型拉取适配该游戏版本的版本号列表</summary>
    private async Task<IReadOnlyList<string>> FetchLoaderVersionsAsync(
        string kind, string gameVersion, CancellationToken ct = default)
    {
        switch (kind)
        {
            case "fabric":
                {
                    // API 已按新到旧返回
                    var loaders = await new FabricMetaClient(_http).GetLoadersForGameAsync(gameVersion, ct);
                    return loaders.Select(l => l.Version).Take(50).ToList();
                }

            case "forge":
                return await new ForgeMavenSource(_http, MirrorEnabled).GetLoaderVersionsAsync(gameVersion, ct);

            case "neoforge":
                return await new NeoForgeMavenSource(_http, MirrorEnabled).GetLoaderVersionsAsync(gameVersion, ct);

            case "optifine":
                {
                    var versions = await new OptiFineClient(_http).GetVersionsAsync(gameVersion, ct);
                    return versions.Select(v => v.VersionTag).ToList();
                }

            default:
                return [];
        }
    }

    // ── 已安装版本管理 ────────────────────────────────

    /// <summary>
    /// 顶部刷新按钮
    /// </summary>
    [RelayCommand]
    private async Task RefreshCurrentAsync()
    {
        if (ActiveTab == 0)
        {
            RefreshInstalledVersions();
        }
        else
        {
            await RefreshVersionsAsync();
        }
    }

    /// <summary>刷新按钮的提示文案</summary>
    public string RefreshTooltip => ActiveTab == 0
        ? "重新扫描本地已安装的版本"
        : "重新获取可下载的游戏版本列表";

    [RelayCommand]
    private void RefreshInstalled() => RefreshInstalledVersions();

    private void RefreshInstalledVersions()
    {
        InstalledVersions.Clear();

        if (LauncherLocator.Validate(MinecraftDirectory) is not null)
        {
            return;
        }

        try
        {
            foreach (var version in InstalledVersionScanner.Scan(MinecraftDirectory))
            {
                InstalledVersions.Add(new InstalledVersionViewModel(version));
            }

            Log($"已安装版本：{InstalledVersions.Count} 个");
        }
        catch (Exception ex)
        {
            Log($"扫描已安装版本失败：{ex.Message}");
        }
    }

    partial void OnSelectedInstalledChanged(InstalledVersionViewModel? value)
    {
        IsRenameMode = false;
        RenameError = null;
        OnPropertyChanged(nameof(HasSelectedInstalled));
        OnPropertyChanged(nameof(ShowInstalledPanel));
        OnPropertyChanged(nameof(ShowPlaceholder));

        if (value is null)
        {
            return;
        }

        // 父版本
        var parent = value.Version.InheritsFrom ?? value.Id;

        // 当前已装的加载器与版本
        var kind = value.Version.IsLoaderVersion ? value.Version.LoaderKind : null;
        var loaderVersion = value.Version.IsLoaderVersion ? value.Version.LoaderVersion : null;

        Log($"已选中 {value.Id}，加载 {(kind is null ? "加载器" : kind)} 的可选版本…");

        _ = LoadLoaderVersionsAsync(parent, kind, loaderVersion);
    }

    [RelayCommand]
    private void BeginRename()
    {
        if (SelectedInstalled is null)
        {
            return;
        }

        RenameInput = SelectedInstalled.Id;
        RenameError = null;
        IsRenameMode = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenameMode = false;
        RenameError = null;
    }

    [RelayCommand]
    private void ConfirmRename()
    {
        if (SelectedInstalled is not { } target)
        {
            return;
        }

        var error = VersionRenamer.ValidateName(MinecraftDirectory, target.Id, RenameInput);
        if (error is not null)
        {
            RenameError = error;
            return;
        }

        try
        {
            var oldId = target.Id;
            var renamed = VersionRenamer.Rename(MinecraftDirectory, oldId, RenameInput);

            Log($"已重命名：{oldId} → {renamed.Id}");
            IsRenameMode = false;
            RenameError = null;

            RefreshInstalled();
            SelectedInstalled = InstalledVersions.FirstOrDefault(v => v.Id == renamed.Id);
        }
        catch (Exception ex)
        {
            RenameError = ex.Message;
            Log($"重命名失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除已安装的版本。
    /// </summary>
    [RelayCommand]
    private async Task DeleteInstalledAsync()
    {
        if (IsBusy || SelectedInstalled is not { } target)
        {
            return;
        }

        var dependents = InstalledVersionScanner.Scan(MinecraftDirectory)
            .Where(v => string.Equals(v.InheritsFrom, target.Id, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Id)
            .ToList();

        if (dependents.Count > 0)
        {
            Log($"拒绝删除 {target.Id}：存在 {dependents.Count} 个依赖它的子版本");

            ShowResult(
                $"无法删除：{target.Id}",
                "存在依赖的子版本：" + string.Join("、", dependents) +
                "\n\n请先删除这些子版本。",
                success: false);

            return;
        }

        var confirmed = await ShowConfirmAsync(
            "确认删除",
            $"将从磁盘删除版本「{target.Id}」。\n\n" +
            "该版本目录下的 mods / config / saves 等数据会一并删除，且不可恢复。\n\n继续？");

        if (!confirmed)
        {
            Log("已取消删除");
            return;
        }

        try
        {
            var dir = Path.Combine(MinecraftDirectory, "versions", target.Id);

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            Log($"已删除版本：{target.Id}");
            StatusText = "已删除";

            RefreshInstalled();
            SelectedInstalled = null;
        }
        catch (Exception ex)
        {
            StatusText = "删除失败";
            Log($"删除 {target.Id} 失败：{ex.Message}");

            ShowResult(
                "删除失败",
                $"{target.Id} 可能正被占用（例如启动器正在使用它）。\n\n{ex.Message}",
                success: false);
        }
    }
    partial void OnNewVersionNameChanged(string value) => ValidateNewVersionName();

    /// <summary>
    /// 应用已安装版本上的加载器变更
    /// </summary>
    [RelayCommand]
    private async Task ApplyInstalledChangesAsync()
    {
        if (IsBusy || SelectedInstalled is not { } target)
        {
            return;
        }

        var current = target.Version;
        var chosen = Loaders.FirstOrDefault(l => l.IsSelected && l.IsSelectable);

        if (chosen is null)
        {
            Log("未选择任何加载器，没有可应用的变更");
            StatusText = "请先勾选一个加载器";
            return;
        }

        var targetKind = chosen.Kind;
        var targetVersion = chosen.RequestedVersion;
        var currentKind = current.IsLoaderVersion ? current.LoaderKind : null;

        var sameKind = string.Equals(currentKind, targetKind, StringComparison.OrdinalIgnoreCase);
        var sameVersion = targetVersion is null ||
                          string.Equals(targetVersion, current.LoaderVersion, StringComparison.OrdinalIgnoreCase);

        if (sameKind && sameVersion)
        {
            Log("加载器与版本均无变化");
            StatusText = "无变化";
            return;
        }

        var parent = current.InheritsFrom ?? current.Id;

        // 组装确认信息
        var currentDesc = current.IsLoaderVersion ? current.LoaderDisplay : "无加载器（原版）";
        var targetDesc = $"{chosen.DisplayName} {targetVersion ?? "最新适配版本"}";

        var sb = new StringBuilder();
        sb.AppendLine($"版本：{current.Id}");
        sb.AppendLine($"当前：{currentDesc}");
        sb.AppendLine($"目标：{targetDesc}");
        sb.AppendLine();

        if (current.IsLoaderVersion)
        {
            sb.AppendLine("将安装新版本，把 mods / config / saves 等用户数据迁移过去，");
            sb.AppendLine("然后删除旧版本目录。");
        }
        else
        {
            sb.AppendLine("将在现有原版版本之上安装该加载器，生成一个新的加载器版本。");
            sb.AppendLine("原版版本本身不会被删除。");
        }

        sb.AppendLine();
        sb.Append("此操作不可撤销，确认继续？");

        if (!await ShowConfirmAsync("确认更新加载器", sb.ToString()))
        {
            Log("已取消更新");
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        LogLines.Clear();

        var progressVm = OpenDownloadOverlay("正在更新加载器");
        var progress = (IDownloadProgress)progressVm;
        var versionsBefore = SnapshotVersionDirs();

        var oldId = current.Id;
        var updateOk = false;
        var wasCancelled = false;

        try
        {
            Log($"开始更新：{oldId} → {targetDesc}");

            progressVm.PlanTasks([targetVersion is null
                ? $"安装 {chosen.DisplayName}"
                : $"安装 {chosen.DisplayName} {targetVersion}"]);
            progressVm.StartTask(0);

            var newId = await InstallLoaderByKindAsync(targetKind, parent, targetVersion, progress, _cts.Token);

            progressVm.CompleteTask(0, newId is not null);

            if (newId is null)
            {
                StatusText = "更新失败";
                return;
            }

            if (!string.Equals(newId, oldId, StringComparison.OrdinalIgnoreCase))
            {
                var moved = VersionDataMigrator.Migrate(MinecraftDirectory, oldId, newId);
                if (moved.Count > 0)
                {
                    Log($"已迁移用户数据：{string.Join("、", moved.Take(12))}" +
                        (moved.Count > 12 ? $" 等 {moved.Count} 项" : ""));
                }

                // 只删加载器版本
                if (current.IsLoaderVersion)
                {
                    var oldDir = Path.Combine(MinecraftDirectory, "versions", oldId);
                    if (Directory.Exists(oldDir))
                    {
                        Directory.Delete(oldDir, recursive: true);
                        Log($"已删除旧版本目录：{oldId}");
                    }
                }
            }

            RefreshInstalled();
            SelectedInstalled = InstalledVersions.FirstOrDefault(v => v.Id == newId);
            StatusText = "更新完成";
            ProgressPercent = 100;
            updateOk = true;
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            StatusText = "已取消";
            Log("操作已取消");

            // 回退
            var createdVersions = SnapshotVersionDirs()
                .Except(versionsBefore, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (createdVersions.Count > 0)
            {
                RollbackNewVersions(createdVersions);
            }
        }
        catch (Exception ex)
        {
            StatusText = "更新失败";
            Log($"错误：{ex.Message}");

            // 失败回滚
            var created = SnapshotVersionDirs()
                .Except(versionsBefore, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (created.Count > 0)
            {
                Log("安装失败，正在回滚本次创建的内容…");
                RollbackNewVersions(created);
            }
        }
        finally
        {
            progressVm.Finish(updateOk, updateOk ? "加载器已更新" : "未完成，请查看上方文件状态与错误");
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;

            // 收起进度层并弹结果提示
            CloseDownloadOverlay();

            if (wasCancelled)
            {
                // 用户取消
            }
            else if (updateOk)
            {
                ShowResult(
                    "更新完成",
                    $"「{oldId}」已更新为 {targetDesc}。\n" +
                    (current.IsLoaderVersion
                        ? "原有的 mods / config / saves 已迁移到新版本目录。"
                        : "原版版本保持不变，已新建加载器版本。") +
                    "\n\n回到启动器刷新版本列表即可看到。",
                    success: true);
            }
            else
            {
                ShowResult(
                    "安装失败",
                    "你可以打开日志查看具体原因，或复制日志后粘出去问问 AI。",
                    success: false,
                    offerLog: true);
            }
        }
    }

    /// <summary>
    /// 把已安装版本的加载器换到另一个版本。
    /// </summary>
    [RelayCommand]

    private void ValidateNewVersionName()
    {
        if (string.IsNullOrWhiteSpace(NewVersionName))
        {
            NewVersionNameError = null;
            return;
        }

        // currentId 传空串：新增
        NewVersionNameError = VersionRenamer.ValidateName(MinecraftDirectory, "", NewVersionName);
    }

    // ── 安装 ──────────────────────────────────────────

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (SelectedVersion is not { } target)
        {
            StatusText = "请先选择游戏版本";
            return;
        }

        if (LauncherLocator.Validate(MinecraftDirectory) is { } invalid)
        {
            StatusText = invalid;
            Log($"无法开始：{invalid}");
            return;
        }

        var chosen = Loaders.Where(l => l.IsSelected && l.IsAvailable).ToList();
        var needsVanilla = !IsVanillaInstalled(target.Id);

        // 冲突名称
        if (VersionRenamer.ValidateName(MinecraftDirectory, "", NewVersionName) is { } nameError)
        {
            NewVersionNameError = nameError;
            StatusText = "版本名称不可用";
            Log($"无法开始：{nameError}");
            return;
        }

        if (!needsVanilla && chosen.Count == 0)
        {
            StatusText = "该版本已安装，且未选择任何加载器";
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        LogLines.Clear();

        var progressVm = OpenDownloadOverlay($"正在安装 {target.Id}");
        var progress = (IDownloadProgress)progressVm;
        var versionsBefore = SnapshotVersionDirs();
        var installOk = false;
        var wasCancelled = false;

        // 预先列出全部任务
        var taskNames = new List<string>();
        if (needsVanilla)
        {
            taskNames.Add($"下载游戏本体 {target.Id}");
        }

        foreach (var loader in chosen)
        {
            var pin = loader.RequestedVersion;
            taskNames.Add(pin is null ? $"安装 {loader.DisplayName}" : $"安装 {loader.DisplayName} {pin}");
        }

        progressVm.PlanTasks(taskNames);
        var taskIndex = 0;

        try
        {
            if (needsVanilla)
            {
                StatusText = $"正在安装 {target.Id}…";
                Log($"开始安装原版 {target.Id}");

                var installer = new Installer(
                    _http, CurrentSource, CurrentMirror,
                    concurrency: _config.Concurrency,
                    slowThresholdBytesPerSecond: SlowThresholdBytesPerSecond,
                    onNotice: msg => Dispatcher.UIThread.Post(() => Log(msg)));
                progressVm.StartTask(taskIndex);

                var result = await installer.InstallAsync(
                    new InstallRequest(target.Id, MinecraftDirectory), progress, _cts.Token);
                progressVm.CompleteTask(taskIndex, result.Success); taskIndex++;

                Log(result.Success
                    ? $"原版安装完成：下载 {result.FilesDownloaded} 个，跳过 {result.FilesSkipped} 个"
                    : $"原版安装失败：{result.FilesFailed} 个文件出错");

                if (!result.Success)
                {
                    foreach (var f in result.Failures.Take(10))
                    {
                        Log($"  ✗ {f.Job.DisplayName}: {f.Error}");
                    }

                    StatusText = "原版安装未完成，已中止后续步骤";
                    return;
                }

                target.IsInstalled = true;
            }

            string? lastProducedId = needsVanilla ? target.Id : null;

            foreach (var loader in chosen)
            {
                progressVm.StartTask(taskIndex);

                var produced = await InstallLoaderAsync(loader, target.Id, progress, _cts.Token);
                progressVm.CompleteTask(taskIndex, produced is not null);
                taskIndex++;

                if (produced is not null)
                {
                    lastProducedId = produced;
                }
            }

            // 用户指定名称
            if (lastProducedId is not null &&
                !string.Equals(lastProducedId, NewVersionName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    VersionRenamer.Rename(MinecraftDirectory, lastProducedId, NewVersionName);
                    Log($"已重命名：{lastProducedId} → {NewVersionName}");
                }
                catch (Exception ex)
                {
                    Log($"重命名失败（版本已安装，名称仍为 {lastProducedId}）：{ex.Message}");
                }
            }

            RefreshInstalled();
            UpdateDetailForSelection();
            StatusText = "全部完成";
            ProgressPercent = 100;
            installOk = true;
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            StatusText = "已取消";
            Log("操作已取消");

            // 回退
            var createdVersions = SnapshotVersionDirs()
                .Except(versionsBefore, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (createdVersions.Count > 0)
            {
                RollbackNewVersions(createdVersions);
            }
        }
        catch (Exception ex)
        {
            StatusText = "安装失败";
            Log($"错误：{ex.Message}");

            // 失败回滚
            var created = SnapshotVersionDirs()
                .Except(versionsBefore, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (created.Count > 0)
            {
                Log("安装失败，正在回滚本次创建的内容…");
                RollbackNewVersions(created);
            }
        }
        finally
        {
            progressVm.Finish(installOk, installOk ? "安装完成" : "未完成，请查看上方文件状态与错误");
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;

            // 收起进度层并弹结果提示
            CloseDownloadOverlay();

            if (wasCancelled)
            {
                // 用户取消
            }
            else if (installOk)
            {
                ShowResult(
                    "安装完成",
                    $"「{NewVersionName}」已经可以启动。\n\n回到启动器刷新版本列表即可看到该版本。",
                    success: true);
            }
            else
            {
                ShowResult(
                    "安装失败",
                    "你可以打开日志查看具体原因，或复制日志后粘出去问问 AI。",
                    success: false,
                    offerLog: true);
            }
        }
    }

    private Task<string?> InstallLoaderAsync(
        LoaderOptionViewModel loader, string gameVersion, IDownloadProgress progress, CancellationToken ct)
        => InstallLoaderByKindAsync(loader.Kind, gameVersion, loader.RequestedVersion, progress, ct);

    /// <summary>按加载器类型安装</summary>
    private async Task<string?> InstallLoaderByKindAsync(
        string kind, string gameVersion, string? pinned, IDownloadProgress progress, CancellationToken ct)
    {
        var displayName = Loaders.FirstOrDefault(l => l.Kind == kind)?.DisplayName ?? kind;

        StatusText = pinned is null ? $"正在安装 {displayName}…" : $"正在安装 {displayName} {pinned}…";
        Log(pinned is null
            ? $"开始安装 {displayName}（自动选最新适配版本）"
            : $"开始安装 {displayName}，指定版本 {pinned}");

        switch (kind)
        {
            case "fabric":
                {
                    var installer = new FabricInstaller(_http, CurrentSource, concurrency: _config.Concurrency);
                    var r = await installer.InstallAsync(gameVersion, pinned, MinecraftDirectory, progress, ct);
                    Log($"Fabric 完成：{r.VersionId}（{r.LoaderVersion}）");
                    return r.VersionId;
                }

            case "forge":
            case "neoforge":
                {
                    var source = kind == "forge"
                        ? (IMavenLoaderSource)new ForgeMavenSource(_http, MirrorEnabled)
                        : new NeoForgeMavenSource(_http, MirrorEnabled);

                    var installer = new MavenLoaderInstaller(
                        source, _http, log: msg => Dispatcher.UIThread.Post(() => Log(msg)));

                    var r = await installer.InstallAsync(gameVersion, pinned, MinecraftDirectory, progress, ct);
                    Log($"{r.LoaderType} 完成：{r.VersionId}");
                    return r.VersionId;
                }

            case "optifine":
                {
                    var installer = new OptiFineInstaller(
                        _http, log: msg => Dispatcher.UIThread.Post(() => Log(msg)));

                    var r = await installer.InstallAsync(gameVersion, pinned, MinecraftDirectory, progress, ct);
                    Log($"OptiFine 完成：{r.VersionId}");
                    return r.VersionId;
                }

            default:
                Log($"未知加载器：{kind}");
                return null;
        }
    }

    /// <summary>原版是否就位</summary>
    private bool IsVanillaInstalled(string mcVersion)
        => !string.IsNullOrWhiteSpace(MinecraftDirectory) &&
           File.Exists(Path.Combine(MinecraftDirectory, "versions", mcVersion, mcVersion + ".json"));

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenDirectory()
    {
        if (string.IsNullOrWhiteSpace(MinecraftDirectory) || !Directory.Exists(MinecraftDirectory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = MinecraftDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log($"打开目录失败：{ex.Message}");
        }
    }

    // ── 辅助 ──────────────────────────────────────────

    /// <summary>
    /// 主下载源
    /// </summary>
    private IDownloadSource CurrentSource =>
        MirrorEnabled && !AutoSwitchMirror ? new BmclapiSource() : new MojangSource();

    /// <summary>备用镜像源</summary>
    private IDownloadSource? CurrentMirror =>
        MirrorEnabled && AutoSwitchMirror ? new BmclapiSource() : null;

    /// <summary>慢阈值，0 表示不启用自动切源</summary>
    private long SlowThresholdBytesPerSecond =>
        MirrorEnabled && AutoSwitchMirror
            ? (long)(Math.Max(0, SlowThresholdKBps) * 1024)
            : 0;

    /// <summary>
    /// 打开独立的进度窗口
    /// </summary>
    /// <summary>打开覆盖层式下载进度面板</summary>
    private ProgressWindowViewModel OpenDownloadOverlay(string title)
    {
        var vm = new ProgressWindowViewModel(title, () => _cts?.Cancel(), MinimizeDownloadOverlay);
        DownloadProgress = vm;
        ShowDownloadOverlay = true;
        IsOverlayMinimized = false;
        return vm;
    }

    /// <summary>收起到右下角状态栏</summary>
    [RelayCommand]
    private void MinimizeDownloadOverlay()
    {
        if (DownloadProgress is null)
        {
            return;
        }

        ShowDownloadOverlay = false;
        IsOverlayMinimized = true;
    }

    /// <summary>重新调出进度覆盖层</summary>
    [RelayCommand]
    private void RestoreDownloadOverlay()
    {
        if (!CanRestoreOverlay)
        {
            return;
        }

        ShowDownloadOverlay = true;
        IsOverlayMinimized = false;
    }

    /// <summary>收起进度覆盖层</summary>
    private void CloseDownloadOverlay()
    {
        DownloadProgress = null;
        ShowDownloadOverlay = false;
        IsOverlayMinimized = false;
    }

    // ── 取消回退 ──────────────────────────────────────

    /// <summary>取消后回退：删除本次操作新建的版本目录。
    /// </summary>
    private void RollbackNewVersions(IEnumerable<string> versionIds)
    {
        foreach (var id in versionIds)
        {
            var dir = Path.Combine(MinecraftDirectory, "versions", id);

            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                Log($"已回退：删除新建的版本目录 {id}");
            }
            catch (Exception ex)
            {
                Log($"回退 {id} 失败，可手动删除 {dir}：{ex.Message}");
            }
        }

        RefreshInstalled();
    }

    /// <summary>当前 versions 下的目录名集合</summary>
    private HashSet<string> SnapshotVersionDirs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versionsDir = Path.Combine(MinecraftDirectory, "versions");

        if (!Directory.Exists(versionsDir))
        {
            return set;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                set.Add(Path.GetFileName(dir));
            }
        }
        catch
        {
        }

        return set;
    }
    /// <summary>创建进度桥接</summary>
    private ObservableProgress CreateProgress() => new(snapshot =>
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressPercent = snapshot.Percent;
            ProgressText = $"{(snapshot.Skipped > 0 ? $"已跳过 {snapshot.Skipped}  " : "")}" +
                           $"{snapshot.Completed}/{snapshot.Total}  {snapshot.BytesDisplay}" +
                           (snapshot.Failed > 0 ? $"  失败 {snapshot.Failed}" : "");
            CurrentFileText = snapshot.CurrentFile ?? "";
        });
    });

    private HttpFetcher CreateHttp()
    {
        var http = new HttpFetcher(maxConnections: _config.Concurrency);

        // 网络日志
        http.Traced += trace =>
        {
            if (NetworkLogEnabled)
            {
                Log(trace.ToLine());
            }
        };

        return http;
    }

    private HashSet<string> ScanInstalledVersions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (string.IsNullOrWhiteSpace(MinecraftDirectory))
            {
                return set;
            }

            var versionsDir = Path.Combine(MinecraftDirectory, "versions");
            if (!Directory.Exists(versionsDir))
            {
                return set;
            }

            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                var name = Path.GetFileName(dir);
                if (File.Exists(Path.Combine(dir, name + ".json")))
                {
                    set.Add(name);
                }
            }
        }
        catch
        {
        }

        return set;
    }

    // 日志批量刷新
    private readonly Queue<string> _pendingLogs = new();
    private bool _logFlushScheduled;

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        lock (_pendingLogs)
        {
            _pendingLogs.Enqueue(line);

            // 已经排队等待刷新
            if (_logFlushScheduled)
            {
                return;
            }

            _logFlushScheduled = true;
        }

        Dispatcher.UIThread.Post(FlushLogs, DispatcherPriority.Background);
    }

    /// <summary>把积压的日志一次性并入 UI 集合，并重建可复制的全文</summary>
    private void FlushLogs()
    {
        string[] batch;

        lock (_pendingLogs)
        {
            batch = _pendingLogs.ToArray();
            _pendingLogs.Clear();
            _logFlushScheduled = false;
        }

        if (batch.Length == 0)
        {
            return;
        }

        foreach (var line in batch)
        {
            LogLines.Add(line);
        }

        while (LogLines.Count > 300)
        {
            LogLines.RemoveAt(0);
        }

        LogText = string.Join(Environment.NewLine, LogLines);
    }

    /// <summary>供 View 层记录日志</summary>
    public void LogManual(string message) => Log(message);

    public void Dispose()
    {
        _http.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
