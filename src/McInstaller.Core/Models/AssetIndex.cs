using System.Text.Json.Serialization;

namespace McInstaller.Core.Models;

/// <summary>
/// 资源索引文件（assets/indexes/&lt;id&gt;.json）。
/// objects 的 key 是原始路径（如 icons/icon_128x128.png），
/// 落盘路径由 value.hash 的前两位分桶决定，与原始路径无关。
/// </summary>
public sealed class AssetIndexFile
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; set; } = [];
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
