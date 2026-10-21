using System.Text.Json.Nodes;
using McInstaller.Core.Models;

namespace McInstaller.Core.VersionManagement;

/// <summary>本地 versions/ 下的一个已安装版本。</summary>
public sealed record InstalledVersion(
    string Id,
    string DirectoryPath,
    string JsonPath,
    string Type,
    string? InheritsFrom,
    string? MainClass,
    string LoaderKind,
    string? LoaderVersion,
    bool HasClientJar,
    long JarSizeBytes)
{
    /// <summary>界面显示用的加载器描述。</summary>
    public string LoaderDisplay => LoaderKind switch
    {
        "fabric" => $"Fabric {LoaderVersion}",
        "forge" => $"Forge {LoaderVersion}",
        "neoforge" => $"NeoForge {LoaderVersion}",
        "optifine" => $"OptiFine {LoaderVersion}",
        _ => "原版",
    };

    public bool IsLoaderVersion => LoaderKind != "none";

    /// <summary>是否是一个加载器叠加版本（有继承）。</summary>
    public bool IsDerived => !string.IsNullOrEmpty(InheritsFrom);
}

/// <summary>
/// 扫描本地 .minecraft/versions/ 下已安装的版本。
///
/// 判定依据全部来自版本 JSON 自身：
///   - mainClass 与 libraries 决定属于哪个加载器
///   - inheritsFrom 决定是否是从原版派生出来的加载器版本
/// 不做目录名猜测 —— 用户可能重命名过版本。
/// </summary>
public static class InstalledVersionScanner
{
    public static IReadOnlyList<InstalledVersion> Scan(string minecraftDirectory)
    {
        var result = new List<InstalledVersion>();

        if (string.IsNullOrWhiteSpace(minecraftDirectory))
        {
            return result;
        }

        var versionsDir = Path.Combine(minecraftDirectory, "versions");
        if (!Directory.Exists(versionsDir))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");

            // 没有同名 json 的目录启动器不会识别，直接跳过
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                var version = Parse(id, dir, jsonPath);
                if (version is not null)
                {
                    result.Add(version);
                }
            }
            catch
            {
                // 单个版本 JSON 损坏不应影响整个列表
                result.Add(new InstalledVersion(
                    id, dir, jsonPath, "unknown", null, null, "none", null, false, 0));
            }
        }

        return result
            .OrderByDescending(v => v.IsLoaderVersion)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static InstalledVersion? Parse(string id, string directory, string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var node = JsonNode.Parse(json);

        if (node is not JsonObject obj)
        {
            return null;
        }

        var type = obj["type"]?.GetValue<string>() ?? "unknown";
        var inheritsFrom = obj["inheritsFrom"]?.GetValue<string>();
        var mainClass = obj["mainClass"]?.GetValue<string>() ?? "";

        var jarPath = Path.Combine(directory, id + ".jar");
        var hasJar = File.Exists(jarPath);
        var jarSize = hasJar ? new FileInfo(jarPath).Length : 0;

        var (kind, version) = DetectLoader(id, obj, mainClass);

        return new InstalledVersion(
            id, directory, jsonPath, type, inheritsFrom, mainClass,
            kind, version, hasJar, jarSize);
    }

    /// <summary>
    /// 从 mainClass、libraries 与目录名推断加载器类型和版本。
    ///
    /// 两步：先查 libraries 里的 maven 坐标（最准），拿不到再用 mainClass 判类型、
    /// 从目录名抽版本。
    ///
    /// 必须从目录名回退的原因：NeoForge 的版本 JSON 里**没有** net.neoforged:neoforge 这个主 artifact，
    /// 只列了 fml 等组件库，靠坐标无法得到版本号。
    /// </summary>
    private static (string Kind, string? Version) DetectLoader(string id, JsonObject obj, string mainClass)
    {
        // 先看 libraries 里的 maven 坐标，最准确
        if (obj["libraries"] is JsonArray libraries)
        {
            foreach (var lib in libraries)
            {
                var name = lib?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var parts = name.Split(':');
                if (parts.Length < 3)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "net.fabricmc" when parts[1] == "fabric-loader":
                        return ("fabric", parts[2]);

                    case "net.neoforged" when parts[1] == "neoforge":
                        return ("neoforge", parts[2]);

                    case "net.minecraftforge" when parts[1] == "forge":
                        // 坐标是 1.21.11-61.2.4，只保留后面那段
                        {
                            var dash = parts[2].IndexOf('-', StringComparison.Ordinal);
                            return ("forge", dash > 0 ? parts[2][(dash + 1)..] : parts[2]);
                        }

                    case "optifine" when parts[1].Equals("OptiFine", StringComparison.OrdinalIgnoreCase):
                        return ("optifine", parts[2]);
                }
            }
        }

        // 库列表没命中时退回 mainClass 特征，版本号尽量从目录名抽
        if (mainClass.Contains("neoforged", StringComparison.OrdinalIgnoreCase))
        {
            return ("neoforge", ExtractFromName(id, @"^neoforge-(?<v>.+)$"));
        }

        if (mainClass.Contains("fabricmc", StringComparison.OrdinalIgnoreCase))
        {
            // fabric-loader-0.19.5-1.21.11 → 0.19.5
            return ("fabric", ExtractFromName(id, @"^fabric-loader-(?<v>.+?)-[^-]+$"));
        }

        if (mainClass.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase))
        {
            // 1.21.11-forge-61.2.4 → 61.2.4
            return ("forge", ExtractFromName(id, @"-forge-(?<v>[^-]+)$"));
        }

        if (mainClass.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase))
        {
            // 1.21.11-OptiFine_HD_U_J9 → HD_U_J9
            return ("optifine", ExtractFromName(id, @"-OptiFine_(?<v>.+)$"));
        }

        return ("none", null);
    }

    private static string? ExtractFromName(string name, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, pattern);
        return match.Success ? match.Groups["v"].Value : null;
    }
}
