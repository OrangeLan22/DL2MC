using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace McInstaller.App.Services;

/// <summary>
/// 用户协议文本的极简 Markdown 渲染
///
/// </summary>
public static partial class AgreementMarkdown
{
    private const string DangerSentence = "不用于私服引流、未成年诱导充值、违规联机等场景";

    public sealed record Block(
        string Text, bool IsListItem, bool IsDanger, bool IsHeading, int HeadingLevel = 0);

    /// <summary>读原文</summary>
    public static string LoadRawText()
    {
        var uri = new Uri("avares://DL2MC/docs/agreement.md");
        using var stream = Avalonia.Platform.AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    ///  Markdown 拆块
    /// </summary>
    public static IReadOnlyList<Block> Parse(string markdown)
    {
        var blocks = new List<Block>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (line.Trim().Length == 0)
            {
                continue;
            }

            var trimmed = line.TrimStart();

            // # / ## / ### 标题
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                {
                    level++;
                }

                var heading = trimmed[level..].Trim();

                if (heading.Length > 0)
                {
                    blocks.Add(new Block(heading, false, false, true, Math.Min(level, 3)));
                    continue;
                }
            }

            var isListItem = trimmed.StartsWith("- ", StringComparison.Ordinal);

    
            var content = isListItem ? trimmed[2..] : trimmed;

            if (content.Contains(DangerSentence, StringComparison.Ordinal))
            {
                blocks.Add(new Block(content, IsListItem: true, IsDanger: true, IsHeading: false));
                continue;
            }

            if (isListItem)
            {
                blocks.Add(new Block(content, IsListItem: true, IsDanger: false, IsHeading: false));
                continue;
            }

            // 小标题
            var isHeading = HeadingPattern().IsMatch(trimmed);

            blocks.Add(new Block(trimmed, IsListItem: false, IsDanger: false, IsHeading: isHeading));
        }

        return blocks;
    }

    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex HeadingPattern();
}
