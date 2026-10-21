using System.Text.Json.Nodes;

namespace McInstaller.Core.VersionManagement;

/// <summary>
/// 已安装版本的重命名。
///
/// 改名不只是移动目录，必须同步三处：
///   1. 目录名
///   2. JSON 文件名
///   3. JSON 内的 id 字段
///
/// 还要处理一个容易被忽略的连带影响：**其他版本可能通过 inheritsFrom 引用它**
/// （例如 OptiFine 版本继承 Forge 版本）。改名后这些引用会失效，
/// 表现为启动时父版本找不到，因此必须级联更新。
/// </summary>
public static class VersionRenamer
{
    /// <summary>
    /// 校验新名称。返回 null 表示可用，否则返回可直接显示给用户的原因。
    /// </summary>
    public static string? ValidateName(string minecraftDirectory, string currentId, string? newId)
    {
        if (string.IsNullOrWhiteSpace(newId))
        {
            return "版本名不能为空";
        }

        newId = newId.Trim();

        if (newId == currentId)
        {
            return null;
        }

        if (newId.Length > 80)
        {
            return "版本名过长（最多 80 个字符）";
        }

        if (newId is "." or "..")
        {
            return "版本名不能是 . 或 ..";
        }

        if (newId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "版本名不能包含 \\ / : * ? \" < > | 等字符";
        }

        // 开头或结尾的空白/点在某些环境下会被系统吃掉，导致实际目录名与预期不符
        if (newId != newId.Trim() || newId.EndsWith('.'))
        {
            return "版本名不能以空格或点结尾";
        }

        if (Directory.Exists(Path.Combine(minecraftDirectory, "versions", newId)))
        {
            return $"已存在同名版本「{newId}」";
        }

        return null;
    }

    /// <summary>
    /// 执行重命名。调用前应先用 <see cref="ValidateName"/> 校验。
    /// </summary>
    public static InstalledVersion Rename(string minecraftDirectory, string oldId, string newId)
    {
        newId = newId.Trim();

        if (oldId == newId)
        {
            var existing = InstalledVersionScanner.Scan(minecraftDirectory)
                .FirstOrDefault(v => v.Id == oldId)
                ?? throw new InvalidOperationException($"找不到版本 {oldId}");

            return existing;
        }

        if (ValidateName(minecraftDirectory, oldId, newId) is { } error)
        {
            throw new InvalidOperationException(error);
        }

        var versionsDir = Path.Combine(minecraftDirectory, "versions");
        var oldDir = Path.Combine(versionsDir, oldId);
        var newDir = Path.Combine(versionsDir, newId);
        var oldJson = Path.Combine(oldDir, oldId + ".json");

        if (!File.Exists(oldJson))
        {
            throw new InvalidOperationException($"版本 {oldId} 缺少 {oldId}.json，无法重命名");
        }

        // 先把内容读进内存，再动磁盘 —— 避免中途失败留下半改名状态
        var node = JsonNode.Parse(File.ReadAllText(oldJson)) as JsonObject
            ?? throw new InvalidOperationException($"版本 {oldId} 的 JSON 结构异常");

        node["id"] = newId;
        var newJsonContent = node.ToJsonString(JsonDefaults.Write);

        Directory.Move(oldDir, newDir);

        // 移动后旧 json 文件仍在（只是换了父目录），要先删掉再写新名字的
        var movedJson = Path.Combine(newDir, oldId + ".json");
        var targetJson = Path.Combine(newDir, newId + ".json");

        if (File.Exists(movedJson) && !string.Equals(movedJson, targetJson, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(movedJson);
        }

        File.WriteAllText(targetJson, newJsonContent);

        // client jar 也是以 id 命名的
        var movedJar = Path.Combine(newDir, oldId + ".jar");
        if (File.Exists(movedJar))
        {
            File.Move(movedJar, Path.Combine(newDir, newId + ".jar"), overwrite: true);
        }

        UpdateInheritanceReferences(versionsDir, oldId, newId);

        return InstalledVersionScanner.Scan(minecraftDirectory)
            .FirstOrDefault(v => v.Id == newId)
            ?? throw new InvalidOperationException($"重命名后未能读回版本 {newId}");
    }

    /// <summary>
    /// 把其他版本里指向 oldId 的 inheritsFrom 改成 newId。
    /// 不处理的话，依赖它的版本会启动失败（父版本找不到）。
    /// </summary>
    private static void UpdateInheritanceReferences(string versionsDir, string oldId, string newId)
    {
        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");

            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(File.ReadAllText(jsonPath)) is not JsonObject obj)
                {
                    continue;
                }

                if (obj["inheritsFrom"]?.GetValue<string>() != oldId)
                {
                    continue;
                }

                obj["inheritsFrom"] = newId;
                File.WriteAllText(jsonPath, obj.ToJsonString(JsonDefaults.Write));
            }
            catch
            {
                // 单个版本损坏不影响其余版本
            }
        }
    }

    /// <summary>
    /// 生成一个不与现有目录冲突的名称，用于「加入新版本」时的默认值。
    /// 例：1.21.11 → 1.21.11-2 → 1.21.11-3
    /// </summary>
    public static string MakeUniqueName(string minecraftDirectory, string baseName)
    {
        var versionsDir = Path.Combine(minecraftDirectory, "versions");

        if (!Directory.Exists(Path.Combine(versionsDir, baseName)))
        {
            return baseName;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!Directory.Exists(Path.Combine(versionsDir, candidate)))
            {
                return candidate;
            }
        }

        return $"{baseName}-{Guid.NewGuid():N}"[..40];
    }

    /// <summary>该名称当前是否已被占用。</summary>
    public static bool IsTaken(string minecraftDirectory, string name)
        => Directory.Exists(Path.Combine(minecraftDirectory, "versions", name));
}
