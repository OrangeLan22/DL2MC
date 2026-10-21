using McInstaller.Core.Models;
using McInstaller.Core.Sources;
using McInstaller.Core.Util;

namespace McInstaller.Core.Pipeline;

/// <summary>
/// 把版本详情中的 libraries 数组解析为下载任务。
///
/// 两个必须做对的点：
///   1. rules 求值（见 RuleEvaluator）
///   2. 落盘路径优先取 artifact.path，缺失时才用 Maven 坐标推导
///      —— Fabric 的 library 不提供 path 字段，且全部自带 url
/// </summary>
public sealed class LibraryResolver(PlatformInfo platform)
{
    public IReadOnlyList<DownloadJob> Resolve(
        VersionDetail detail, IDownloadSource source, IDownloadSource? mirror = null)
    {
        var jobs = new List<DownloadJob>();

        foreach (var library in detail.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, platform))
            {
                continue;
            }

            AddArtifact(jobs, library, source, mirror);
            AddNativeClassifier(jobs, library, source, mirror);
        }

        return jobs;
    }

    private void AddArtifact(
        List<DownloadJob> jobs, Library library, IDownloadSource source, IDownloadSource? mirror)
    {
        var artifact = library.Downloads?.Artifact;

        // 只有 classifiers 而没有主 artifact 的库（旧版 MC 的 jinput / twitch 等）
        // 主 jar 本身并不存在，硬推路径去下必然 404。
        // 这类库只需要下载 natives，交给 AddNativeClassifier 处理。
        if (artifact is null && library.Downloads?.Classifiers is { Count: > 0 })
        {
            return;
        }

        var relativePath = artifact?.Path;

        if (string.IsNullOrEmpty(relativePath))
        {
            // 无法从 path 得到时用 Maven 坐标推导（Fabric 等第三方库的常态）
            try
            {
                relativePath = MavenCoordinate.Parse(library.Name).ToRelativePath();
            }
            catch (FormatException)
            {
                return;
            }
        }

        var uri = source.GetLibraryUri(library, artifact ?? new DownloadArtifact(), relativePath);

        // Fabric 把 sha1/size 放在 library 层级，原版放在 downloads.artifact 里
        var sha1 = artifact?.Sha1 ?? library.Sha1;
        var size = artifact?.Size ?? library.Size;

        // 备用源：官方太慢时换镜像。注意 library 自带 url（如 Fabric 的 maven）
        // 两边会得到同一个地址，镜像并不加速它，但保留不会出错。
        Func<Uri>? fallback = mirror is null
            ? null
            : () => mirror.GetLibraryUri(library, artifact ?? new DownloadArtifact(), relativePath);

        jobs.Add(new DownloadJob($"libraries/{relativePath}", uri, sha1, size, library.Name, fallback));
    }

    /// <summary>
    /// 旧版本（1.x 早中期）的本地库：natives 字段指明平台对应的 classifier 键。
    /// 现代版本（26.x）已无此字段，实测 114 个 library 中为 0。
    /// </summary>
    private void AddNativeClassifier(
        List<DownloadJob> jobs, Library library, IDownloadSource source, IDownloadSource? mirror)
    {
        if (library.Natives is not { Count: > 0 } natives)
        {
            return;
        }

        if (!natives.TryGetValue(platform.OsName, out var classifierKey) ||
            string.IsNullOrEmpty(classifierKey))
        {
            return;
        }

        // natives-windows-${arch} → natives-windows-64 / -32
        var archBits = platform.OsArch == "x86" ? "32" : "64";
        classifierKey = classifierKey.Replace("${arch}", archBits, StringComparison.Ordinal);

        if (library.Downloads?.Classifiers is not { } classifiers ||
            !classifiers.TryGetValue(classifierKey, out var native))
        {
            return;
        }

        if (string.IsNullOrEmpty(native.Path))
        {
            return;
        }

        var uri = source.GetLibraryUri(library, native, native.Path);
        Func<Uri>? fallback = mirror is null
            ? null
            : () => mirror.GetLibraryUri(library, native, native.Path!);

        jobs.Add(new DownloadJob(
            $"libraries/{native.Path}", uri, native.Sha1, native.Size, $"{library.Name}:{classifierKey}", fallback));
    }
}
