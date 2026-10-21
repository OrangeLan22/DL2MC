using McInstaller.Core.Pipeline;

namespace McInstaller.Core.Progress;

/// <summary>
/// 下载进度回调。实现方需自行保证节流，回调可能来自多个线程。
/// </summary>
public interface IDownloadProgress
{
    /// <summary>任务规划完成，给出总量。</summary>
    void OnPlanned(int totalFiles, long totalBytes);

    /// <summary>
    /// 任务清单已确定。UI 可据此预先展示完整文件列表，
    /// 之后通过 OnFileCompleted / OnFileFailed 更新单项状态。
    /// </summary>
    void OnJobsPlanned(IReadOnlyList<DownloadJob> jobs)
    {
        // 默认不处理，兼容只关心聚合进度的实现
    }

    /// <summary>单个文件完成（skipped 表示校验通过被跳过）。</summary>
    void OnFileCompleted(DownloadJob job, bool skipped, long bytes);

    /// <summary>单个文件最终失败（已用尽重试）。</summary>
    void OnFileFailed(DownloadJob job, Exception error);
}

/// <summary>什么都不做的实现，便于测试与 CLI 静默模式。</summary>
public sealed class NullProgress : IDownloadProgress
{
    public static readonly NullProgress Instance = new();

    public void OnPlanned(int totalFiles, long totalBytes) { }

    public void OnFileCompleted(DownloadJob job, bool skipped, long bytes) { }

    public void OnFileFailed(DownloadJob job, Exception error) { }
}
