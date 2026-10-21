using System.Text.RegularExpressions;

namespace McInstaller.Core.Java;

/// <summary>一个可用的 Java 运行时。</summary>
public sealed record JavaRuntime(
    string Path,
    int MajorVersion,
    string FullVersion,
    string Source);

/// <summary>
/// Java 运行时探测。
///
/// 为什么需要它：Forge / NeoForge 的 installer 本身就是一个 Java 程序，
/// 必须用能跑它的 JVM 来执行安装。而游戏本身对 Java 版本还有下限要求，
/// 这个下限可以直接从版本 JSON 的 javaVersion.majorVersion 读到（26.3 是 25，1.21.11 是 21）。
///
/// 探测不到时按既定决策「明确报错并提示用户安装」，不做自动下载 JRE。
/// </summary>
public sealed partial class JavaLocator(ProcessRunner runner)
{
    [GeneratedRegex(@"version\s+""(?<ver>[^""]+)""")]
    private static partial Regex VersionPattern();

    /// <summary>
    /// 找出满足最低主版本要求的最高版本 Java。
    /// 返回 null 表示没找到合格的运行时。
    /// </summary>
    public async Task<JavaRuntime?> FindAsync(
        int minimumMajor,
        IEnumerable<string>? extraCandidates = null,
        CancellationToken ct = default)
    {
        var all = await DiscoverAsync(extraCandidates, ct).ConfigureAwait(false);

        return all
            .Where(r => r.MajorVersion >= minimumMajor)
            .OrderByDescending(r => r.MajorVersion)
            .FirstOrDefault();
    }

    /// <summary>探测所有能读出版本的 Java 运行时。</summary>
    public async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(
        IEnumerable<string>? extraCandidates = null,
        CancellationToken ct = default)
    {
        var candidates = new List<(string Path, string Source)>();
        candidates.AddRange(CollectCandidates());

        if (extraCandidates is not null)
        {
            candidates.AddRange(extraCandidates.Select(p => (p, "用户指定")));
        }

        var results = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, source) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!seen.Add(path) || !File.Exists(path))
            {
                continue;
            }

            var runtime = await ProbeAsync(path, source, ct).ConfigureAwait(false);
            if (runtime is not null)
            {
                results.Add(runtime);
            }
        }

        return results;
    }

    private IEnumerable<(string Path, string Source)> CollectCandidates()
    {
        var exe = OperatingSystem.IsWindows() ? "java.exe" : "java";

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            yield return (System.IO.Path.Combine(javaHome, "bin", exe), "JAVA_HOME");
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate))
            {
                yield return (candidate, "PATH");
            }
        }

        foreach (var (root, source) in WellKnownRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(root);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var dir in dirs)
            {
                var candidate = System.IO.Path.Combine(dir, "bin", exe);
                if (File.Exists(candidate))
                {
                    yield return (candidate, source);
                }
            }
        }
    }

    private static IEnumerable<(string Root, string Source)> WellKnownRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            yield return (System.IO.Path.Combine(programFiles, "Java"), "Program Files\\Java");
            yield return (System.IO.Path.Combine(programFiles, "Eclipse Adoptium"), "Eclipse Adoptium");
            yield return (System.IO.Path.Combine(programFiles, "Eclipse Foundation"), "Eclipse Foundation");
            yield return (System.IO.Path.Combine(programFiles, "Zulu"), "Zulu");
            yield return (System.IO.Path.Combine(programFiles, "Microsoft"), "Microsoft JDK");
            yield return (System.IO.Path.Combine(programFiles, "BellSoft"), "BellSoft");
            yield return (System.IO.Path.Combine(programFiles, "Amazon Corretto"), "Corretto");
            yield return (System.IO.Path.Combine(programFilesX86, "Java"), "Program Files (x86)\\Java");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ("/Library/Java/JavaVirtualMachines", "系统 JVM");
            yield return ("/System/Volumes/Data/Library/Java/JavaVirtualMachines", "系统 JVM");
        }
        else
        {
            yield return ("/usr/lib/jvm", "系统 JVM");
            yield return ("/usr/java", "系统 JVM");
        }
    }

    private async Task<JavaRuntime?> ProbeAsync(string path, string source, CancellationToken ct)
    {
        var result = await runner
            .RunAsync(path, ["-version"], timeout: TimeSpan.FromSeconds(20), ct: ct)
            .ConfigureAwait(false);

        // java -version 历史上输出到 stderr，新版可能到 stdout —— 两个都查
        var text = string.Concat(result.StandardError, "\n", result.StandardOutput);

        var match = VersionPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var full = match.Groups["ver"].Value;
        var major = ParseMajor(full);

        return major > 0 ? new JavaRuntime(path, major, full, source) : null;
    }

    /// <summary>
    /// 从版本串解析主版本号。
    /// "21.0.2" → 21；"1.8.0_291" → 8（老式 1.x 命名）。
    /// </summary>
    internal static int ParseMajor(string fullVersion)
    {
        var parts = fullVersion.Split('.', '_', '-', '+');

        if (parts.Length == 0 || !int.TryParse(parts[0], out var first))
        {
            return 0;
        }

        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second))
        {
            return second;
        }

        return first;
    }
}
