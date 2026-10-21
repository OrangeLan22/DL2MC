using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace McInstaller.App.Services;

/// <summary>GitHub 上的最新 Release 信息。</summary>
public sealed record UpdateInfo(
    string Tag,
    string Name,
    string HtmlUrl,
    string Body,
    DateTimeOffset? PublishedAt);

/// <summary>
/// 通过 GitHub Releases 检查新版本。只做通知，不自动下载。
/// </summary>
public static partial class UpdateChecker
{
    private const string Owner = "OrangeLan22";
    private const string Repo = "DL2MC";

    private static readonly Uri ApiUri =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    /// <summary>
    /// 取最新 Release。仓库还没发布任何 Release 时返回 null（404），
    /// 网络失败也返回 null —— 更新检查不该影响主流程。
    /// </summary>
    public static async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

            // GitHub API 强制要求 User-Agent
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DL2MC-UpdateChecker");

            using var response = await http.GetAsync(ApiUri, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var release = await response.Content
                .ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
                .ConfigureAwait(false);

            if (release?.TagName is null || release.HtmlUrl is null)
            {
                return null;
            }

            return new UpdateInfo(
                release.TagName,
                release.Name ?? release.TagName,
                release.HtmlUrl,
                release.Body ?? string.Empty,
                release.PublishedAt);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>判断远端版本是否比本地新。</summary>
    public static bool IsNewer(string remoteVersion, string localVersion)
    {
        var remote = ParseVersion(remoteVersion);
        var local = ParseVersion(localVersion);

        // ValueTuple 没有比较运算符，逐段比
        if (remote.Major != local.Major)
        {
            return remote.Major > local.Major;
        }

        return remote.Minor != local.Minor
            ? remote.Minor > local.Minor
            : remote.Patch > local.Patch;
    }

    /// <summary>
    /// 从任意版本串里抽出三元组比较。
    /// 兼容 "beta 0.2"、"v0.2"、"0.2.1"、"release-1.0.3" 这些写法。
    /// </summary>
    private static (int Major, int Minor, int Patch) ParseVersion(string text)
    {
        var numbers = NumberPattern().Matches(text);

        var major = numbers.Count > 0 && int.TryParse(numbers[0].Value, out var a) ? a : 0;
        var minor = numbers.Count > 1 && int.TryParse(numbers[1].Value, out var b) ? b : 0;
        var patch = numbers.Count > 2 && int.TryParse(numbers[2].Value, out var c) ? c : 0;

        return (major, minor, patch);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }
}
