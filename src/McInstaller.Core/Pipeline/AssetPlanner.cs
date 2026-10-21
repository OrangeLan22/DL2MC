using McInstaller.Core.Models;
using McInstaller.Core.Net;
using McInstaller.Core.Sources;

namespace McInstaller.Core.Pipeline;

/// <summary>
/// 把资源索引展开成下载任务。
///
/// 关键点：
///   - objects 的 key 是原始路径，但落盘路径由 hash 的前两位分桶决定
///   - 不同原始路径可能共享同一 hash，必须按 hash 去重
///   - 进度总量应以 assetIndex.totalSize 为准（1.21.11 约 462 MB / 4000+ 对象）
/// </summary>
public sealed class AssetPlanner(HttpFetcher http)
{
    public async Task<AssetPlan> PlanAsync(
        string versionId,
        AssetIndexRef indexRef,
        IDownloadSource source,
        IDownloadSource? mirror = null,
        CancellationToken ct = default)
    {
        var indexUri = source.GetAssetIndexUri(versionId, indexRef)
            ?? throw new InvalidOperationException($"版本 {versionId} 的 assetIndex 缺少 url，无法下载");

        var json = await http.GetStringAsync(indexUri, ct).ConfigureAwait(false);
        var index = JsonDefaults.Deserialize<AssetIndexFile>(json);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<DownloadJob>(index.Objects.Count);

        foreach (var (originalPath, obj) in index.Objects)
        {
            if (string.IsNullOrEmpty(obj.Hash) || !seen.Add(obj.Hash))
            {
                continue;
            }

            // 资源对象是体积最大的一块（1.21.11 约 462 MB），镜像收益最明显
            Func<Uri>? fallback = mirror is null ? null : () => mirror.GetAssetObjectUri(obj.Hash);

            jobs.Add(DownloadJob.ForAssetObject(
                obj.Hash, obj.Size, source.GetAssetObjectUri(obj.Hash), fallback));
        }

        return new AssetPlan(
            jobs,
            index,
            indexRef.Id,
            indexRef.Virtual,
            indexRef.MapToResources,
            jobs.Sum(j => j.Size ?? 0),
            json);
    }
}

public sealed record AssetPlan(
    IReadOnlyList<DownloadJob> Jobs,
    AssetIndexFile Index,
    string IndexId,
    bool NeedsVirtual,
    bool NeedsResources,
    long TotalBytes,
    string RawJson)
{
    /// <summary>
    /// 旧版本需要把对象按原始路径展开成实体文件。
    /// 1.7+ 通常两个开关都为 false。
    /// </summary>
    public bool NeedsExpansion => NeedsVirtual || NeedsResources;

    public IEnumerable<(string OriginalPath, AssetObject Object)> ExpansionTargets()
    {
        if (!NeedsExpansion)
        {
            yield break;
        }

        foreach (var (originalPath, obj) in Index.Objects)
        {
            yield return (originalPath, obj);
        }
    }
}
