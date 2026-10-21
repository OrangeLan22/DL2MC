namespace McInstaller.Core.Pipeline;

/// <summary>
/// 一个待下载文件。
/// RelativePath 是相对于 .minecraft 根目录的路径，与具体源无关 —— 换源不改变落盘位置。
///
/// FallbackUriFactory 用于「官方源太慢时切到镜像」：
/// 为 null 表示该资源没有镜像可用（如 Modrinth 的 mod 文件）。
/// </summary>
public sealed record DownloadJob(
    string RelativePath,
    Uri Uri,
    string? Sha1,
    long? Size,
    string DisplayName,
    Func<Uri>? FallbackUriFactory = null)
{
    /// <summary>该文件是否有备用源。</summary>
    public bool HasFallback => FallbackUriFactory is not null;

    /// <summary>资源对象专用：校验与命名都基于 hash。</summary>
    public static DownloadJob ForAssetObject(
        string hash, long size, Uri uri, Func<Uri>? fallbackFactory = null)
        => new($"assets/objects/{hash[..2]}/{hash}", uri, hash, size, hash, fallbackFactory);

    public override string ToString() => $"{DisplayName} → {RelativePath}";
}
