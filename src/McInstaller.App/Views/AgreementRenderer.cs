using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using McInstaller.App.Services;

namespace McInstaller.App.Views;

/// <summary>
/// 把协议 Markdown 渲染成一组 TextBlock
/// </summary>
public static class AgreementRenderer
{
    /// <summary>清空 host 并按 blocks 逐块渲染</summary>
    public static void Render(StackPanel host, string markdown)
    {
        host.Children.Clear();

        foreach (var block in AgreementMarkdown.Parse(markdown))
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                FontSize = 12.5,
                Foreground = FindBrush(host, "PrimaryTextBrush", Colors.Gray),
            };

            if (block.HeadingLevel > 0)
            {
                // Markdown 标题：# 最大，### 最小
                text.FontWeight = FontWeight.Bold;
                text.FontSize = block.HeadingLevel switch
                {
                    1 => 16,
                    2 => 14,
                    _ => 13,
                };
                text.Margin = new Thickness(0, block.HeadingLevel == 1 ? 2 : 12, 0, 2);
            }
            else if (block.IsHeading)
            {
                text.FontWeight = FontWeight.Bold;
                text.FontSize = 13;
                text.Margin = new Thickness(0, 10, 0, 0);
            }
            else if (block.IsListItem)
            {
                text.Margin = new Thickness(14, 0, 0, 0);
            }

            // 需要标红的那条
            if (block.IsDanger)
            {
                text.Foreground = FindBrush(host, "WarningBrush", Colors.OrangeRed);
                text.FontWeight = FontWeight.Bold;
            }

            AppendRichText(text, block.Text);

            host.Children.Add(text);
        }
    }

    /// <summary>解析粗体，</summary>
    private static void AppendRichText(TextBlock target, string source)
    {
        target.Inlines ??= new InlineCollection();

        var index = 0;

        while (index < source.Length)
        {
            var start = source.IndexOf("**", index, StringComparison.Ordinal);

            if (start < 0)
            {
                target.Inlines.Add(new Run(source[index..]));
                return;
            }

            var end = source.IndexOf("**", start + 2, StringComparison.Ordinal);

            if (end < 0)
            {
                target.Inlines.Add(new Run(source[index..]));
                return;
            }

            if (start > index)
            {
                target.Inlines.Add(new Run(source[index..start]));
            }

            target.Inlines.Add(new Run(source[(start + 2)..end])
            {
                FontWeight = FontWeight.Bold,
            });

            index = end + 2;
        }
    }

    private static IBrush FindBrush(Control host, string key, Color fallback)
        => host.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}
