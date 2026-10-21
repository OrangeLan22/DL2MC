using System.Text.Json.Serialization;

namespace McInstaller.Core.Models;

/// <summary>
/// 版本详情 JSON。
/// 字段以 2026-10 实测的 26.3 / 1.21.11 为准，未实测到的字段一律可空以免解析崩溃。
/// </summary>
public sealed class VersionDetail
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>
    /// 版本继承的父版本 id。加载器（Fabric 等）产出的版本会带此字段。
    /// 已实测支持继承链，但子版本仍必须具备 mainClass / arguments / libraries。
    /// </summary>
    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexRef? AssetIndex { get; set; }

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; set; } = [];

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    [JsonPropertyName("arguments")]
    public VersionArguments? Arguments { get; set; }

    /// <summary>旧版（≤1.12）使用该字段而非 arguments.game。</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersion? JavaVersion { get; set; }

    [JsonPropertyName("minimumLauncherVersion")]
    public int? MinimumLauncherVersion { get; set; }

    /// <summary>
    /// 各源格式不统一（Fabric 用 +0000，Mojang 用 +00:00），走宽容转换器。
    /// 解析失败时为 null，不影响安装。
    /// </summary>
    [JsonPropertyName("releaseTime")]
    [JsonConverter(typeof(LenientDateTimeOffsetConverter))]
    public DateTimeOffset? ReleaseTime { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary>资源对象总字节数。进度条的总量应以它为准。</summary>
    [JsonPropertyName("totalSize")]
    public long? TotalSize { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>旧版本：需展开到 assets/virtual/&lt;indexId&gt;/ 下。</summary>
    [JsonPropertyName("virtual")]
    public bool Virtual { get; set; }

    /// <summary>更老版本：需展开到 resources/ 下。</summary>
    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; set; }
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public DownloadArtifact? Client { get; set; }

    [JsonPropertyName("server")]
    public DownloadArtifact? Server { get; set; }
}

/// <summary>
/// 一个可下载文件。libraries[].downloads.artifact 与 downloads.client 共用此形状。
/// </summary>
public sealed class DownloadArtifact
{
    /// <summary>落盘相对路径。26.x 的 libraries 会直接给出，无需自行推导。</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class Library
{
    /// <summary>Maven 坐标：group:artifact:version[:classifier][@ext]</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    /// <summary>
    /// 非默认 Maven 仓库。Fabric 的 library 全部带此字段（实测 8/8），
    /// 若不使用会导致 Fabric 安装整体失败。
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; set; }

    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    /// <summary>
    /// 校验值与大小也可能直接挂在 library 层级。
    /// Fabric 的 profile 就是这样（实测字段：name/url/md5/sha1/sha256/sha512/size），
    /// 此时 downloads.artifact 为空，解析器必须回退到这里。
    /// </summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadArtifact? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

/// <summary>
/// 平台适配规则。求值方式：从空结果开始遍历，后者覆盖前者。
/// 注意 OS 名称是 "osx" 而非 "macos"。
/// </summary>
public sealed class Rule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("os")]
    public OsRule? Os { get; set; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class VersionArguments
{
    [JsonPropertyName("game")]
    public List<ArgumentEntry> Game { get; set; } = [];

    [JsonPropertyName("jvm")]
    public List<ArgumentEntry> Jvm { get; set; } = [];
}

public sealed class JavaVersion
{
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    /// <summary>该版本要求的最低 Java 主版本号。26.3 → 25，1.21.11 → 21。</summary>
    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

/// <summary>
/// arguments 数组元素：可能是纯字符串，也可能是带 rules 的对象。
/// </summary>
[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed class ArgumentEntry
{
    public string? Value { get; set; }

    public List<string>? Values { get; set; }

    public List<Rule>? Rules { get; set; }
}
