using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using McInstaller.App.Services;

namespace McInstaller.App.Views;

/// <summary>
/// 只读文档窗口，用于展示使用条款、更新日志这类纯文本页面。
/// 内容通过 <see cref="ShowText"/> 或 <see cref="ShowMarkdown"/> 填充。
/// </summary>
public partial class DocumentWindow : Window
{
    public DocumentWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }

    /// <summary>设置窗口标题（同时更新任务栏/标题栏与页面内标题）。</summary>
    public void SetTitle(string title)
    {
        Title = $"DL2MC — {title}";
        TitleText.Text = title;
    }

    /// <summary>按协议同样的 Markdown 规则渲染正文。</summary>
    public void ShowMarkdown(string markdown)
        => AgreementRenderer.Render(ContentHost, markdown);

    /// <summary>纯文本占位内容（如「暂无更新日志」）。</summary>
    public void ShowText(string text)
    {
        ContentHost.Children.Clear();

        IBrush foreground = Brushes.Gray;
        if (this.TryFindResource("SecondaryTextBrush", out var brush) && brush is IBrush found)
        {
            foreground = found;
        }

        ContentHost.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            FontSize = 12.5,
            Foreground = foreground,
        });
    }

    /// <summary>
    /// 加载「更新日志」目录下的全部版本说明。
    /// 每个版本一个 md 文件，按文件名倒序拼接（beta-0.2 排在 beta-0.1 前面）。
    /// </summary>
    public void ShowChangelog()
    {
        SetTitle("更新日志");

        var files = ChangelogService.GetFiles();

        if (files.Count == 0)
        {
            ShowText("暂无更新日志。");
            return;
        }

        ContentHost.Children.Clear();

        for (var i = 0; i < files.Count; i++)
        {
            var section = new StackPanel { Spacing = 9 };
            AgreementRenderer.Render(section, ChangelogService.Read(files[i]));
            ContentHost.Children.Add(section);

            // 版本之间加一条分隔线，最后一个不加
            if (i < files.Count - 1)
            {
                IBrush lineBrush = Brushes.Gray;
                if (this.TryFindResource("AppBorderBrush", out var brush) && brush is IBrush found)
                {
                    lineBrush = found;
                }

                ContentHost.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 18, 0, 6),
                    Background = lineBrush,
                });
            }
        }
    }
    /// <summary>加载内置的常见问题。</summary>
    public void ShowFaq()
    {
        SetTitle("常见问题");

        var uri = new Uri("avares://DL2MC/docs/faq.md");

        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            ShowMarkdown(reader.ReadToEnd());
        }
        catch
        {
            ShowText("常见问题内容读取失败。");
        }
    }
    /// <summary>直接加载内置的用户协议。</summary>
    public void ShowAgreement()
    {
        SetTitle("使用条款");
        ShowMarkdown(AgreementMarkdown.LoadRawText());
    }
}
