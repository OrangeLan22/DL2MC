using System.Text.Json.Serialization;
using McInstaller.Core.Net;

namespace McInstaller.Core.Loaders;

/// <summary>BMCLAPI 返回的 OptiFine 条目。</summary>
public sealed class OptiFineVersion
{
    [JsonPropertyName("mcversion")]
    public string McVersion { get; set; } = "";

    /// <summary>HD_U / HD_U_J1 等。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>J9、pre10 等。</summary>
    [JsonPropertyName("patch")]
    public string Patch { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    /// <summary>配套的 Forge 版本，如 "Forge 52.0.16"。同时装 Forge 时需要校验。</summary>
    [JsonPropertyName("forge")]
    public string Forge { get; set; } = "";

    /// <summary>是否预览版（patch 以 pre 开头）。</summary>
    [JsonIgnore]
    public bool IsPreview =>
        Patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase) ||
        Type.Contains("preview", StringComparison.OrdinalIgnoreCase);

    /// <summary>版本标识，形如 HD_U_J9。</summary>
    [JsonIgnore]
    public string VersionTag => $"{Type}_{Patch}";
}

/// <summary>
/// OptiFine 数据源（BMCLAPI）。
///
/// 不走官网：optifine.net 的下载要经过 adfoc.us 广告跳转，其直链依赖页面内嵌的
/// 32 位签名参数，程序化提取等于绕开作者的广告变现 —— 这是本项目最应回避的合规点。
/// BMCLAPI 是社区公开维护的镜像，端点实测见 docs/ENDPOINTS.md §7。
/// </summary>
public sealed class OptiFineClient(HttpFetcher http)
{
    private const string Base = "https://bmclapi2.bangbang93.com";

    /// <summary>列出某个 MC 版本的 OptiFine，正式版优先，同类内按版本号从新到旧。</summary>
    public async Task<IReadOnlyList<OptiFineVersion>> GetVersionsAsync(
        string mcVersion, CancellationToken ct = default)
    {
        var json = await http
            .GetStringAsync(new Uri($"{Base}/optifine/{Uri.EscapeDataString(mcVersion)}"), ct)
            .ConfigureAwait(false);

        var versions = JsonDefaults.Deserialize<List<OptiFineVersion>>(json);

        // BMCLAPI 返回顺序并非版本倒序（实测 1.21.11 列表里 J8 排在 J9 前），
        // 必须自己按 patch 做自然排序，否则会「选到较旧的那个正式版」。
        return versions
            .OrderBy(v => v.IsPreview)
            .ThenByDescending(v => v.Patch, NaturalOrderComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// 自然顺序比较：字母前缀先比，再比数字后缀。
    /// "J9" &lt; "J10"，但字典序会给出相反结果。
    /// </summary>
    private sealed class NaturalOrderComparer : IComparer<string>
    {
        public static readonly NaturalOrderComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return string.CompareOrdinal(x, y);
            }

            var (xPrefix, xNumber) = Split(x);
            var (yPrefix, yNumber) = Split(y);

            var prefixCompare = string.CompareOrdinal(xPrefix, yPrefix);
            return prefixCompare != 0 ? prefixCompare : xNumber.CompareTo(yNumber);
        }

        private static (string Prefix, int Number) Split(string value)
        {
            var i = 0;
            while (i < value.Length && !char.IsDigit(value[i]))
            {
                i++;
            }

            var prefix = value[..i];
            var number = i < value.Length && int.TryParse(value[i..], out var n) ? n : 0;

            return (prefix, number);
        }
    }

    public static Uri GetDownloadUri(string mcVersion, string type, string patch)
        => new($"{Base}/optifine/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}");
}
