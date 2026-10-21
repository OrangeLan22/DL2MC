namespace McInstaller.Core.Loaders;

/// <summary>加载器安装结果，与 Installer 的原版安装结果保持同构。</summary>
public sealed record LoaderInstallResult(
    string LoaderType,
    string VersionId,
    string ParentVersion,
    string? LoaderVersion,
    bool Success,
    string VersionJsonPath,
    int FilesDownloaded,
    int FilesSkipped,
    int FilesFailed,
    long BytesDownloaded,
    IReadOnlyList<Pipeline.DownloadFailure> Failures);

/// <summary>
/// 加载器安装器共同契约。
///
/// 与 Installer（原版）的本质区别：加载器依赖已存在的父版本，
/// 自身通常不产出 client jar，只落一个版本 JSON + 若干库。
/// </summary>
public interface ILoaderInstaller
{
    /// <summary>加载器名称，用于日志与 UI。</summary>
    string Name { get; }

    /// <summary>
    /// 安装加载器。
    /// </summary>
    /// <param name="gameVersion">父版本 id，必须已完整安装。</param>
    /// <param name="loaderVersion">加载器版本；null 表示自动选择。</param>
    /// <param name="minecraftDirectory">目标 .minecraft 目录。</param>
    Task<LoaderInstallResult> InstallAsync(
        string gameVersion,
        string? loaderVersion,
        string minecraftDirectory,
        Progress.IDownloadProgress? progress = null,
        CancellationToken ct = default);
}
