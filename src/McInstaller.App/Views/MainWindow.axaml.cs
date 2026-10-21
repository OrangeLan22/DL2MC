using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using McInstaller.App.ViewModels;

namespace McInstaller.App.Views;

public partial class MainWindow : Window
{
    /// <summary>日志面板高度的可拖范围</summary>
    private const double MinLogHeight = 80;
    private const double MaxLogHeight = 600;

    /// <summary>左侧列表宽度的可拖范围</summary>
    private const double MinLeftWidth = 260;
    private const double MaxLeftWidth = 700;

    private INotifyCollectionChanged? _observedLog;
    private MainViewModel? _observedVm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

#if DEBUG
        // DevTools
        this.AttachDevTools();
#endif

        // 用 lambda 订阅，让编译器推断 DragCompleted 的事件参数类型
        RowSplitter.DragCompleted += (_, _) => PersistLayout();
        ColumnSplitter.DragCompleted += (_, _) => PersistLayout();

        // 点空白处让输入框失焦：隧道阶段先判断落点，
        // 落点不在任何 TextBox 内就主动清掉焦点，否则搜索框会一直保持输入态
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        // 主题切换后重新上色（ActualThemeVariant 变化不会触发资源重查，
        // 因为这里的颜色是代码直接赋给 Border 的）
        ActualThemeVariantChanged += (_, _) => RefreshTabVisuals();
    }

    /// <summary>
    /// 顶部两个 tab 的配色
    /// </summary>
    private void RefreshTabVisuals()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark;

        ApplyTab(InstalledTabBg, vm.IsInstalledTab, dark);
        ApplyTab(AddTabBg, vm.IsAddTab, dark);
    }

    private static void ApplyTab(Border? border, bool active, bool dark)
    {
        if (border is null)
        {
            return;
        }

        // 激活 = 主题主色（亮 #0078D4 / 暗 #D9822B）
        var background = active
            ? (dark ? "#D9822B" : "#0078D4")
            : (dark ? "#35383E" : "#E8EAEF");

        border.Background = new ImmutableSolidColorBrush(Color.Parse(background));

        // 图标与文字都绑定了 $parent[ToggleButton].Foreground
        if (border.Parent is ToggleButton toggle)
        {
            toggle.Foreground = new ImmutableSolidColorBrush(active
                ? Colors.White
                : Color.Parse(dark ? "#E8EAED" : "#1B1F24"));
        }
    }
    /// <summary>
    /// 顶部两个 tab 用 ToggleButton 是为了拿到选中态外观，
    /// 但 ToggleButton 默认允许"再点一下取消选中"。
    /// tab 是单选语义，取消后两个都会变成未选中，所以这里立即复位。
    /// </summary>
    private void OnTabToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: false } toggle)
        {
            toggle.IsChecked = true;
        }
    }
    /// <summary>打开「更新日志」页面</summary>
    private async void OnOpenChangelogClick(object? sender, RoutedEventArgs e)
    {
        var window = new DocumentWindow();
        window.ShowChangelog();

        await window.ShowDialog(this);
    }

    /// <summary>打开「常见问题」页面</summary>
    private async void OnOpenFaqClick(object? sender, RoutedEventArgs e)
    {
        var window = new DocumentWindow();
        window.ShowFaq();

        await window.ShowDialog(this);
    }
    /// <summary>打开作者主页</summary>
    private void OnOpenAuthorSiteClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://orangelan.cn",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开浏览器就算了
        }
    }
    /// <summary>打开「使用条款」页面</summary>
    private async void OnOpenTermsClick(object? sender, RoutedEventArgs e)
    {
        var window = new DocumentWindow();
        window.ShowAgreement();

        await window.ShowDialog(this);
    }
    /// <summary>
    /// 点击 TextBox 以外的区域时，把焦点从输入框移走。
    /// 用隧道阶段处理，不影响按钮等控件自身的点击逻辑。
    /// </summary>
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点在输入框内部（含其子视觉）就保持焦点不动
        if (e.Source is Visual source
            && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            return;
        }

        // 只有当前焦点确实落在某个 TextBox 上时才清理
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            FocusManager.ClearFocus();
        }
    }
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // 日志自动滚到底部
        if (_observedLog is not null)
        {
            _observedLog.CollectionChanged -= OnLogChanged;
        }

        _observedLog = vm.LogLines;
        _observedLog.CollectionChanged += OnLogChanged;

        // 监听日志显示开关与尺寸恢复
        if (_observedVm is not null)
        {
            _observedVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observedVm = vm;
        _observedVm.PropertyChanged += OnViewModelPropertyChanged;

        RefreshTabVisuals();

        RestoreLayoutSizes(vm);

        // 首次进入时触发初始化
        if (vm.InitializeCommand.CanExecute(null))
        {
            vm.InitializeCommand.Execute(null);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowLogPanel) && sender is MainViewModel vm)
        {
            ApplyLogPanelVisibility(vm);
        }

        // tab 切换时重刷顶部按钮配色
        if (e.PropertyName is nameof(MainViewModel.IsInstalledTab) or nameof(MainViewModel.IsAddTab))
        {
            RefreshTabVisuals();
        }
    }

    /// <summary>按配置恢复两个分隔条的尺寸</summary>
    private void RestoreLayoutSizes(MainViewModel vm)
    {
        var leftWidth = Math.Clamp(vm.LeftPanelWidthPx, MinLeftWidth, MaxLeftWidth);
        MainGrid.ColumnDefinitions[0].Width = new GridLength(leftWidth);

        ApplyLogPanelVisibility(vm);
    }

    /// <summary>日志区显隐</summary>
    private void ApplyLogPanelVisibility(MainViewModel vm)
    {
        var row = RootGrid.RowDefinitions[3];

        var splitterRow = RootGrid.RowDefinitions[2];
        splitterRow.Height = vm.ShowLogPanel ? new GridLength(4) : new GridLength(0);

        if (vm.ShowLogPanel)
        {
            row.MinHeight = MinLogHeight;
            row.Height = new GridLength(Math.Clamp(vm.LogPanelHeightPx, MinLogHeight, MaxLogHeight));
        }
        else
        {
            // MinHeight 必须先降到 0，否则设 0 也会被限制回 80
            row.MinHeight = 0;
            row.Height = new GridLength(0);
        }
    }

    /// <summary>拖动分隔条结束后把尺寸写回配置</summary>
    private void PersistLayout()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var logHeight = Math.Clamp(RootGrid.RowDefinitions[3].Height.Value, MinLogHeight, MaxLogHeight);
        var leftWidth = Math.Clamp(MainGrid.ColumnDefinitions[0].Width.Value, MinLeftWidth, MaxLeftWidth);

        vm.SaveLayoutSize(logHeight, leftWidth);
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 等布局完成后再滚
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => LogScroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>展开 / 收起历史目录下拉</summary>
    private void OnToggleDirectoryPopup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsDirectoryPopupOpen = !vm.IsDirectoryPopupOpen;
        }
    }

    /// <summary>历史目录：点击切换到该目录，并收起下拉</summary>
    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string directory } && DataContext is MainViewModel vm)
        {
            vm.SelectDirectoryCommand.Execute(directory);
            vm.IsDirectoryPopupOpen = false;
        }
    }

    /// <summary>历史目录：点 × 从历史中移除</summary>
    private void OnHistoryRemoveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is Button { DataContext: string directory } && DataContext is MainViewModel vm)
        {
            vm.RemoveDirectoryCommand.Execute(directory);
        }
    }

    /// <summary>点击右下角状态文字</summary>
    private void OnStatusTextPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm &&
            vm.RestoreDownloadOverlayCommand.CanExecute(null))
        {
            vm.RestoreDownloadOverlayCommand.Execute(null);
        }
    }
    /// <summary>真正的目录选择器 —— 用户不一定要手动输路径</summary>
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择启动器的 .minecraft 目录",
                AllowMultiple = false,
            });

            if (folders.Count > 0)
            {
                // 触发 setter 会顺带保存配置并刷新已安装标记
                vm.MinecraftDirectory = folders[0].Path.LocalPath;
            }
        }
        catch (Exception ex)
        {
            vm.LogManual($"选择目录失败：{ex.Message}");
        }
    }
}
