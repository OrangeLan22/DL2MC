using McInstaller.Core.Models;

namespace McInstaller.Core.Sources;

/// <summary>
/// 数据来源抽象：把「逻辑资源」映射为「具体 URL」。
/// 官方源与镜像源实现同一接口，切换源不改变下载器逻辑。
/// </summary>
public interface IDownloadSource
{
    /// <summary>显示名称，用于日志与进度提示。</summary>
    string Name { get; }

    /// <summary>版本清单。</summary>
    Uri GetVersionManifestUri();

    /// <summary>版本详情。官方源直接使用清单中给出的 url。</summary>
    Uri GetVersionDetailUri(VersionSummary summary);

    /// <summary>客户端 jar。</summary>
    /// <param name="versionId">版本 id，镜像源需要用它拼路径。</param>
    /// <param name="client">详情中的 downloads.client，官方源需要其中的 url。</param>
    Uri? GetClientJarUri(string versionId, DownloadArtifact? client);

    /// <summary>资源索引文件。</summary>
    Uri? GetAssetIndexUri(string versionId, AssetIndexRef? index);

    /// <summary>资源对象。hash 的前两位同时用于分桶目录与 URL 路径。</summary>
    Uri GetAssetObjectUri(string hash);

    /// <summary>
    /// 库文件。
    /// 优先级：library 自带的 url &gt; artifact.url &gt; 默认 maven / 镜像 maven。
    /// Fabric 的 library 全部自带 url，忽略它会导致 Fabric 安装失败。
    /// </summary>
    Uri GetLibraryUri(Library library, DownloadArtifact artifact, string relativePath);
}
