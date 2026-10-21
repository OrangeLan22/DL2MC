namespace McInstaller.Core.VersionManagement;

/// <summary>
/// 版本数据目录迁移。
///
/// 用途：修改已安装版本的加载器版本时，需要在新版本装上后把旧版本里的用户数据
/// （mods、config、saves、resourcepacks…）搬过去，再删掉旧目录。
///
/// 不迁移的内容：
///   - 版本自身的 &lt;id&gt;.json 与 &lt;id&gt;.jar —— 属于版本本身，新版本会自带
///   - *-natives 目录 —— 启动器会按需重新解压，搬过去反而可能版本错配
/// </summary>
public static class VersionDataMigrator
{
    /// <summary>
    /// 把 fromId 版本目录里的用户数据合并到 toId 版本目录。返回被迁移的条目名。
    /// 目标已存在同名目录时递归合并（不覆盖已存在的文件）。
    /// </summary>
    public static IReadOnlyList<string> Migrate(string minecraftDirectory, string fromId, string toId)
    {
        var fromDir = Path.Combine(minecraftDirectory, "versions", fromId);
        var toDir = Path.Combine(minecraftDirectory, "versions", toId);

        var moved = new List<string>();

        if (!Directory.Exists(fromDir) || !Directory.Exists(toDir))
        {
            return moved;
        }

        foreach (var item in Directory.EnumerateFileSystemEntries(fromDir))
        {
            var name = Path.GetFileName(item);

            // 版本自身文件不迁移
            if (string.Equals(name, fromId + ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, fromId + ".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // natives 由启动器按版本重新生成，不搬
            if (name.Contains("natives", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(item))
            {
                continue;
            }

            var target = Path.Combine(toDir, name);

            try
            {
                if (Directory.Exists(item))
                {
                    if (Directory.Exists(target))
                    {
                        MergeDirectory(item, target);
                        moved.Add(name + "/（合并）");
                    }
                    else
                    {
                        Directory.Move(item, target);
                        moved.Add(name + "/");
                    }
                }
                else
                {
                    File.Copy(item, target, overwrite: false);
                    File.Delete(item);
                    moved.Add(name);
                }
            }
            catch (Exception ex)
            {
                moved.Add($"{name}（失败：{ex.Message}）");
            }
        }

        return moved;
    }

    /// <summary>递归合并目录。目标已存在的同名文件保留不动。</summary>
    private static void MergeDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));

            if (File.Exists(target))
            {
                continue;   // 已有同名文件，保留目标的
            }

            File.Move(file, target);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            MergeDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        // 源目录已空则删掉
        if (!Directory.EnumerateFileSystemEntries(source).Any())
        {
            Directory.Delete(source);
        }
    }
}
