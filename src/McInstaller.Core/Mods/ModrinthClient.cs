using System.Text.Json;
using System.Text.Json.Serialization;
using McInstaller.Core.Net;

namespace McInstaller.Core.Mods;

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = "";

    /// <summary>release | beta | alpha</summary>
    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = "";

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = [];

    /// <summary>该版本是否为主文件之外的附加文件（如 sources jar）。</summary>
    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public sealed class ModrinthFile
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("hashes")]
    public Dictionary<string, string>? Hashes { get; set; }

    public string? Sha1 =>
        Hashes is not null && Hashes.TryGetValue("sha1", out var v) ? v : null;

    public string? Sha512 =>
        Hashes is not null && Hashes.TryGetValue("sha512", out var v) ? v : null;
}

/// <summary>
/// Modrinth API 客户端。
///
/// 选择 Modrinth 而非 CurseForge：后者需要申请 API key 且对第三方客户端有限制，
/// 前者开放免费。实测 api.modrinth.com 首次请求偶发超时，依赖 HttpFetcher 的重试。
/// 端点实测见 docs/ENDPOINTS.md §4。
/// </summary>
public sealed class ModrinthClient(HttpFetcher http)
{
    private const string Base = "https://api.modrinth.com/v2";

    /// <summary>
    /// 查询指定项目在某个游戏版本 + 加载器下的可用版本。
    /// 返回按「优先 release」排序的结果，但不做静默筛选 —— 由调用方决定接受什么。
    /// </summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string slug, string gameVersion, string loader, CancellationToken ct = default)
    {
        var gameVersions = JsonSerializer.Serialize(new[] { gameVersion });
        var loaders = JsonSerializer.Serialize(new[] { loader });

        var uri = new Uri(
            $"{Base}/project/{Uri.EscapeDataString(slug)}/version" +
            $"?game_versions={Uri.EscapeDataString(gameVersions)}" +
            $"&loaders={Uri.EscapeDataString(loaders)}");

        var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
        var versions = JsonDefaults.Deserialize<List<ModrinthVersion>>(json);

        return versions
            .OrderByDescending(v => v.VersionType == "release")
            .ThenByDescending(v => v.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>取项目基本信息，用于确认 slug 是否有效。</summary>
    public async Task<(string Title, string Slug)> GetProjectAsync(string slug, CancellationToken ct = default)
    {
        var uri = new Uri($"{Base}/project/{Uri.EscapeDataString(slug)}");

        // 该端点响应很大（实测 19.6 KB），只取需要的两个字段
        var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? slug : slug;
        var actualSlug = root.TryGetProperty("slug", out var s) ? s.GetString() ?? slug : slug;

        return (title, actualSlug);
    }
}
