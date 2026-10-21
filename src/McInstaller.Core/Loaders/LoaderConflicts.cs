namespace McInstaller.Core.Loaders;

/// <summary>
/// 加载器之间的冲突规则。
///
/// 依据：
///   - Fabric / Forge / NeoForge 是三个互相独立的模组加载器，都要求接管启动入口
///     （各自有自己的 mainClass + tweaker/transformer），同一版本目录里不可能共存。
///   - NeoForge 从 Forge 分叉后与 Forge 也不兼容（同样的类被两套实现占用）。
///   - OptiFine 与 Forge 兼容：官方 jar 内含 OptiFineForgeTweaker。
///   - OptiFine 与 Fabric 冲突：独立运行需要 launchwrapper，而 Fabric 用 knot 入口，
///     官方没有交集方案（社区靠第三方 OptiFabric，不属于官方支持范畴）。
///   - OptiFine 与 NeoForge：官方未声明支持，按冲突处理，避免用户装出一个跑不起来的组合。
///
/// 这些是「同一版本内」的限制。用户仍可以分别装成多个独立版本，各自搭配不同加载器。
/// </summary>
public static class LoaderConflicts
{
    private static readonly string[][] ExclusivePairs =
    [
        ["fabric", "forge"],
        ["fabric", "neoforge"],
        ["forge", "neoforge"],
        ["fabric", "optifine"],
        ["neoforge", "optifine"],
    ];

    /// <summary>两个加载器是否不能同时安装。</summary>
    public static bool AreConflicting(string a, string b)
    {
        foreach (var pair in ExclusivePairs)
        {
            if ((string.Equals(pair[0], a, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(pair[1], b, StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(pair[0], b, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(pair[1], a, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>给出冲突理由，用于界面提示。</summary>
    public static string DescribeConflict(string kind)
    {
        var other = kind switch
        {
            "fabric" => "Forge / NeoForge / OptiFine",
            "forge" => "Fabric / NeoForge",
            "neoforge" => "Fabric / Forge / OptiFine",
            "optifine" => "Fabric / NeoForge",
            _ => "",
        };

        return other.Length == 0 ? "" : $"与 {other} 冲突";
    }
}
