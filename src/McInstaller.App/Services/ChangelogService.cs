using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform;

namespace McInstaller.App.Services;

/// <summary>
/// 读取更新日志
/// </summary>
public static class ChangelogService
{
    private static readonly Uri Folder = new("avares://DL2MC/docs/changelog");

    /// <summary>遍历全部日志</summary>
    public static IReadOnlyList<Uri> GetFiles()
    {
        try
        {
            return AssetLoader
                .GetAssets(Folder, null)
                .Where(u => u.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(u => Path.GetFileName(u.AbsolutePath), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>读取正文。</summary>
    public static string Read(Uri file)
    {
        using var stream = AssetLoader.Open(file);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 获取当前版本号
    /// </summary>
    public static string GetLatestVersion()
    {
        var files = GetFiles();

        if (files.Count == 0)
        {
            // 拿不到返回 unknown
            return "unknown";
        }

        try
        {
            using var stream = AssetLoader.Open(files[0]);
            using var reader = new StreamReader(stream);

            var firstLine = reader.ReadLine();

            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return "unknown";
            }

            return firstLine.TrimStart('#', ' ').Trim();
        }
        catch
        {
            return "unknown";
        }
    }
}
