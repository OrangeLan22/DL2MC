using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using McInstaller.App.Services;

namespace McInstaller.App.Views;

/// <summary>
/// 首次启动的用户协议窗口。三个条件都满足才能点「同意并继续」：
///   1. 窗口打开后已过 10 秒（计时与滚动无关，一出现就开始）
///   2. 正文滚动到最下方
///   3. 手动输入指定句子（禁用粘贴）
/// 关窗或点「不同意」都直接退出程序。
/// </summary>
public partial class AgreementWindow : Window
{
    /// <summary>必须手动输入的句子。</summary>
    private const string RequiredInput = "我已阅读并同意以上全部条款，且仅用于个人正版学习使用";

    /// <summary>打开后需要等待的秒数。</summary>
    private const int DwellSeconds = 10;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private bool _scrolledToBottom;
    private int _secondsLeft = DwellSeconds;
    private bool _accepted;

    public AgreementWindow()
    {
        InitializeComponent();

        RenderAgreement();

        ContentScroll.ScrollChanged += OnScrollChanged;
        ConfirmInput.TextChanged += OnInputChanged;

        // 禁止一切粘贴途径
        ConfirmInput.PastingFromClipboard += OnPasting;

        AcceptButton.Click += (_, _) => Accept();
        DeclineButton.Click += (_, _) => Close();

        _timer.Tick += OnTick;
    }

    /// <summary>是否通过全部条件并同意。</summary>
    public bool Accepted => _accepted;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 计时从窗口出现就开始，不等滚动到底
        _timer.Start();

        // 内容短到不需要滚动时也要能通过「滚到底」这一关
        Dispatcher.UIThread.Post(EvaluateScrollState, DispatcherPriority.Loaded);

        UpdateState();
    }

    /// <summary>把协议文本渲染成一组 TextBlock。</summary>
    private void RenderAgreement()
    {
        var blocks = AgreementMarkdown.Parse(AgreementMarkdown.LoadRawText());

        foreach (var block in blocks)
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                FontSize = 12.5,
                Foreground = FindBrush("PrimaryTextBrush", Colors.Gray),
            };

            if (block.IsHeading)
            {
                text.FontWeight = FontWeight.Bold;
                text.FontSize = 13;
                text.Margin = new Thickness(0, 10, 0, 0);
            }
            else if (block.IsListItem)
            {
                text.Margin = new Thickness(14, 0, 0, 0);
            }

            // 需要标红的那一条：整块用警示色并加粗
            if (block.IsDanger)
            {
                text.Foreground = FindBrush("WarningBrush", Colors.OrangeRed);
                text.FontWeight = FontWeight.Bold;
            }

            // Markdown 的 **粗体** 转成分段 Run
            AppendRichText(text, block.Text);

            ContentHost.Children.Add(text);
        }
    }

    /// <summary>把 **xx** 解析成粗体 Run，其余保持常规。</summary>
    private static void AppendRichText(TextBlock target, string source)
    {
        target.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();

        var index = 0;

        while (index < source.Length)
        {
            var start = source.IndexOf("**", index, StringComparison.Ordinal);

            if (start < 0)
            {
                target.Inlines.Add(new Avalonia.Controls.Documents.Run(source[index..]));
                return;
            }

            var end = source.IndexOf("**", start + 2, StringComparison.Ordinal);

            if (end < 0)
            {
                target.Inlines.Add(new Avalonia.Controls.Documents.Run(source[index..]));
                return;
            }

            if (start > index)
            {
                target.Inlines.Add(new Avalonia.Controls.Documents.Run(source[index..start]));
            }

            target.Inlines.Add(new Avalonia.Controls.Documents.Run(source[(start + 2)..end])
            {
                FontWeight = FontWeight.Bold,
            });

            index = end + 2;
        }
    }

    private IBrush FindBrush(string key, Color fallback)
        => this.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => EvaluateScrollState();

    /// <summary>判断是否已经滚到底部。</summary>
    private void EvaluateScrollState()
    {
        if (_scrolledToBottom)
        {
            return;
        }

        var extent = ContentScroll.Extent.Height;
        var viewport = ContentScroll.Viewport.Height;
        var offset = ContentScroll.Offset.Y;

        // 内容不足以填满一屏时，视为已到底
        if (extent <= viewport + 1 || offset + viewport >= extent - 2)
        {
            _scrolledToBottom = true;
            UpdateState();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _secondsLeft--;

        if (_secondsLeft <= 0)
        {
            _timer.Stop();
            _secondsLeft = 0;
        }

        UpdateState();
    }

    /// <summary>拦截所有粘贴操作。</summary>
    private void OnPasting(object? sender, RoutedEventArgs e) => e.Handled = true;

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => UpdateState();

    /// <summary>
    /// 刷新按钮状态。倒计时未结束时按钮上直接显示剩余秒数，
    /// 但滚动和输入始终可用，不会被锁住。
    /// </summary>
    private void UpdateState()
    {
        var dwellDone = _secondsLeft <= 0;
        var typed = string.Equals(ConfirmInput.Text?.Trim(), RequiredInput, StringComparison.Ordinal);

        AcceptButton.IsEnabled = dwellDone && _scrolledToBottom && typed;

        AcceptButton.Content = dwellDone
            ? "同意并继续"
            : $"同意并继续（{_secondsLeft}s）";

        // 已经输入正确、但还没把正文读完时，明确告诉用户差在哪一步
        ReadFirstHint.IsVisible = typed && !_scrolledToBottom;
    }

    private void Accept()
    {
        _accepted = true;
        _timer.Stop();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
