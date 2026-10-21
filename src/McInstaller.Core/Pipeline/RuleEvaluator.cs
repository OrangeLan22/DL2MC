using McInstaller.Core.Models;

namespace McInstaller.Core.Pipeline;

/// <summary>
/// 版本 JSON 中 rules 数组的求值。
///
/// 语义（Minecraft 官方实现）：
///   - rules 为空或缺失 → 包含
///   - 否则从「不包含」开始遍历规则，命中则把结果置为该规则的 action
///   - 后出现的规则覆盖先出现的
///   - 一条规则的所有条件都满足才算命中；未出现的条件视为不约束
/// </summary>
public static class RuleEvaluator
{
    /// <summary>判断某条目是否适用于当前平台。</summary>
    public static bool IsAllowed(
        IReadOnlyList<Rule>? rules,
        PlatformInfo platform,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var result = false;

        foreach (var rule in rules)
        {
            if (!Matches(rule, platform, features))
            {
                continue;
            }

            result = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static bool Matches(
        Rule rule,
        PlatformInfo platform,
        IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is { Length: > 0 } name &&
                !string.Equals(name, platform.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (os.Arch is { Length: > 0 } arch &&
                !string.Equals(arch, platform.OsArch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (rule.Features is { Count: > 0 } required)
        {
            foreach (var (key, expected) in required)
            {
                var actual = features is not null && features.TryGetValue(key, out var v) && v;
                if (actual != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
