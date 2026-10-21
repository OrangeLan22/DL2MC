using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using McInstaller.Core.Net;
using McInstaller.Core.Progress;

namespace McInstaller.Core.Loaders;

/// <summary>
/// OptiFine 安装器 —— 五个目标中唯一**完全自研安装逻辑**的一个。
///
/// OptiFine 的 jar 是 Swing 安装器，实测没有任何命令行参数（对比 Forge 的 --installClient），
/// 无法静默安装，因此只能自己产出安装结果：
///
///   libraries/optifine/OptiFine/&lt;tag&gt;/OptiFine-&lt;tag&gt;.jar
///   libraries/optifine/launchwrapper-of/&lt;lwVer&gt;/launchwrapper-of-&lt;lwVer&gt;.jar   ← 从 jar 内提取
///   versions/&lt;id&gt;/&lt;id&gt;.json
///
/// 该结构已实测通过：OptiFine 版本能被启动器识别并正常进入主菜单。
///
/// 两个易错点：
///   1. launchwrapper-of 的版本号必须从 jar 内条目名解析，不能硬编码 2.3
///   2. 目录名、文件名、json 里的 id 三者必须一致，否则启动器会归入「不常用版本」
/// </summary>
public sealed class OptiFineInstaller : ILoaderInstaller
{
    private readonly HttpFetcher _http;
    private readonly OptiFineClient _client;
    private readonly Action<string>? _log;

    public OptiFineInstaller(HttpFetcher http, Action<string>? log = null)
    {
        _http = http;
        _client = new OptiFineClient(http);
        _log = log;
    }

    public string Name => "OptiFine";

    public async Task<LoaderInstallResult> InstallAsync(
        string gameVersion,
        string? loaderVersion,
        string minecraftDirectory,
        IDownloadProgress? progress = null,
        CancellationToken ct = default)
    {
        progress ??= NullProgress.Instance;
        var mcDir = Path.GetFullPath(minecraftDirectory);

        EnsureParentInstalled(mcDir, gameVersion);

        var versions = await _client.GetVersionsAsync(gameVersion, ct).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            throw new InvalidOperationException(
                $"BMCLAPI 上没有 Minecraft {gameVersion} 对应的 OptiFine。\n" +
                "OptiFine 更新较慢，较新的游戏版本往往还没有可用版本。");
        }

        var selected = SelectVersion(versions, loaderVersion);

        _log?.Invoke($"OptiFine 版本：{selected.VersionTag}（{selected.Filename}，" +
                     $"共 {versions.Count} 个可选，其中正式版 {versions.Count(v => !v.IsPreview)} 个）");

        if (!string.IsNullOrEmpty(selected.Forge) && selected.Forge != "Forge N/A")
        {
            _log?.Invoke($"该 OptiFine 配套 {selected.Forge}；若要与 Forge 同装，需注意版本匹配");
        }

        progress.OnPlanned(1, 0);

        var tempDir = Path.Combine(Path.GetTempPath(), "downlog2mc-of-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var ofJarPath = Path.Combine(tempDir, selected.Filename);
            var downloadUri = OptiFineClient.GetDownloadUri(gameVersion, selected.Type, selected.Patch);

            _log?.Invoke($"下载：{downloadUri}");
            var bytes = await _http.DownloadToFileAsync(downloadUri, ofJarPath, ct).ConfigureAwait(false);
            _log?.Invoke($"已下载：{bytes / 1024} KB");

            // 库 artifact 版本号用 <mc>_<type>_<patch>，版本 id 用 <mc>-OptiFine_<type>_<patch>
            var libVersion = $"{gameVersion}_{selected.Type}_{selected.Patch}";
            var versionId = $"{gameVersion}-OptiFine_{selected.Type}_{selected.Patch}";

            // 从 jar 内提取 launchwrapper-of
            //
            // 1.8 及更早的 OptiFine 是「xdelta 补丁式」安装器：jar 里没有
            // launchwrapper-of，只有 1798 个 patch/*.xdelta 和 OptiFineForgeTweaker。
            // 这类版本在装了 Forge 的前提下可以直接作为 mod 放进 mods/ 目录。
            (string Version, byte[] Content)? extracted = null;
            try
            {
                extracted = ExtractLaunchWrapper(ofJarPath);
            }
            catch (InvalidOperationException)
            {
                // 交给下面的分支处理
            }

            if (extracted is null)
            {
                return await InstallAsModAsync(
                    ofJarPath, selected, versionId, gameVersion, mcDir, progress, ct)
                    .ConfigureAwait(false);
            }

            var (launchWrapperVersion, launchWrapperBytes) = extracted.Value;
            _log?.Invoke($"从 jar 内提取 launchwrapper-of-{launchWrapperVersion}（{launchWrapperBytes.Length / 1024} KB）");

            //    库 artifact 版本号用 <mc>_<type>_<patch>（如 1.21.11_HD_U_J9），
            //    这是社区惯例的产物形状。
            //    而版本 id 用 <mc>-OptiFine_<type>_<patch>，两者不能混用。

            var ofLibDir = Path.Combine(mcDir, "libraries", "optifine", "OptiFine", libVersion);
            Directory.CreateDirectory(ofLibDir);
            var ofLibPath = Path.Combine(ofLibDir, $"OptiFine-{libVersion}.jar");
            File.Copy(ofJarPath, ofLibPath, overwrite: true);

            var lwLibDir = Path.Combine(mcDir, "libraries", "optifine", "launchwrapper-of", launchWrapperVersion);
            Directory.CreateDirectory(lwLibDir);
            var lwLibPath = Path.Combine(lwLibDir, $"launchwrapper-of-{launchWrapperVersion}.jar");
            await File.WriteAllBytesAsync(lwLibPath, launchWrapperBytes, ct).ConfigureAwait(false);

            _log?.Invoke($"库已就位：OptiFine-{libVersion}.jar、launchwrapper-of-{launchWrapperVersion}.jar");

            var jsonPath = await WriteVersionJsonAsync(
                mcDir, versionId, gameVersion, libVersion, launchWrapperVersion, ct).ConfigureAwait(false);

            _log?.Invoke($"OptiFine 安装完成，版本目录：{versionId}");

            progress.OnFileCompleted(
                new Pipeline.DownloadJob(ofLibPath, downloadUri, null, bytes, selected.Filename),
                skipped: false,
                bytes);

            return new LoaderInstallResult(
                LoaderType: Name,
                VersionId: versionId,
                ParentVersion: gameVersion,
                LoaderVersion: selected.VersionTag,
                Success: true,
                VersionJsonPath: jsonPath,
                FilesDownloaded: 2,
                FilesSkipped: 0,
                FilesFailed: 0,
                BytesDownloaded: bytes + launchWrapperBytes.Length,
                Failures: []);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static OptiFineVersion SelectVersion(
        IReadOnlyList<OptiFineVersion> versions, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = versions.FirstOrDefault(v =>
                string.Equals(v.VersionTag, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Patch, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Filename, requested, StringComparison.OrdinalIgnoreCase));

            return match ?? throw new InvalidOperationException(
                $"找不到 OptiFine 版本 {requested}；可用版本：" +
                string.Join(", ", versions.Take(10).Select(v => v.VersionTag)));
        }

        // 默认选最新的正式版（预览版不稳定）
        return versions.FirstOrDefault(v => !v.IsPreview) ?? versions[0];
    }

    /// <summary>
    /// 老版本 OptiFine（1.8 及更早）的安装方式：jar 是补丁式安装器，
    /// 但其中带有 optifine.OptiFineForgeTweaker，装了 Forge 时可以直接当 mod 用。
    /// 这里把它复制到父版本的 mods 目录（遵循版本隔离约定）。
    /// </summary>
    private async Task<LoaderInstallResult> InstallAsModAsync(
        string ofJarPath,
        OptiFineVersion selected,
        string versionId,
        string gameVersion,
        string mcDir,
        IDownloadProgress progress,
        CancellationToken ct)
    {
        if (!HasForgeInChain(mcDir, gameVersion))
        {
            throw new InvalidOperationException(
                $"OptiFine {selected.Type}_{selected.Patch} 是「xdelta 补丁式」安装器，" +
                "需要打补丁修改原版 class，本工具暂不支持自动处理。\n\n" +
                "可选做法：\n" +
                "  1. 先安装 Forge，再重新执行本操作 —— 有 Forge 时 OptiFine 可直接作为 mod 使用；\n" +
                "  2. 手动运行 OptiFine jar 里的安装程序完成安装。");
        }

        // 遵循版本隔离：mods 放在 versions/<父版本>/mods 下
        var modsDir = Path.Combine(mcDir, "versions", gameVersion, "mods");
        Directory.CreateDirectory(modsDir);

        var targetName = $"OptiFine_{gameVersion}_{selected.Type}_{selected.Patch}.jar";
        var targetPath = Path.Combine(modsDir, targetName);

        var bytes = new FileInfo(ofJarPath).Length;
        var skipped = File.Exists(targetPath) && new FileInfo(targetPath).Length == bytes;

        if (!skipped)
        {
            File.Copy(ofJarPath, targetPath, overwrite: true);
        }

        _log?.Invoke(skipped
            ? $"OptiFine 已存在于 mods：{targetName}"
            : $"已将 OptiFine 放入 mods：{targetName}");
        _log?.Invoke("该版本采用 mod 方式加载，不新建版本目录");

        progress.OnFileCompleted(
            new Pipeline.DownloadJob(targetPath, new Uri("about:blank"), null, bytes, targetName),
            skipped,
            skipped ? 0 : bytes);

        return new LoaderInstallResult(
            LoaderType: "optifine",
            ParentVersion: gameVersion,
            LoaderVersion: $"{selected.Type}_{selected.Patch}",
            Success: true,
            VersionJsonPath: string.Empty,
            VersionId: versionId,
            FilesDownloaded: skipped ? 0 : 1,
            FilesSkipped: skipped ? 1 : 0,
            FilesFailed: 0,
            BytesDownloaded: skipped ? 0 : bytes,
            Failures: []);
    }

    /// <summary>沿 inheritsFrom 链判断这个版本是否基于 Forge。</summary>
    private static bool HasForgeInChain(string mcDir, string versionId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = versionId;

        while (!string.IsNullOrEmpty(current) && visited.Add(current))
        {
            var jsonPath = Path.Combine(mcDir, "versions", current, current + ".json");
            if (!File.Exists(jsonPath))
            {
                return false;
            }

            var text = File.ReadAllText(jsonPath);

            if (text.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("OptiFineForge", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                current = doc.RootElement.TryGetProperty("inheritsFrom", out var parent)
                    ? parent.GetString()
                    : null;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 从 OptiFine jar 内提取 launchwrapper-of-&lt;ver&gt;.jar。
    /// 版本号从条目文件名解析 —— 硬编码会在 OptiFine 升级后失效。
    /// </summary>
    private static (string Version, byte[] Content) ExtractLaunchWrapper(string optiFineJarPath)
    {
        using var zip = ZipFile.OpenRead(optiFineJarPath);

        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("launchwrapper-of-", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains('/'));

        if (entry is null)
        {
            throw new InvalidOperationException(
                "OptiFine jar 内没有找到 launchwrapper-of-*.jar，" +
                "该版本可能使用了不同的启动方式，需人工确认。");
        }

        var fileName = Path.GetFileNameWithoutExtension(entry.Name);
        var version = fileName["launchwrapper-of-".Length..];

        using var buffer = new MemoryStream();
        using var stream = entry.Open();
        stream.CopyTo(buffer);

        return (version, buffer.ToArray());
    }

    /// <summary>
    /// 生成版本 JSON。
    /// 结构已手工验证：mainClass 用 launchwrapper 的 Launch，
    /// tweaker 走 arguments.game，两个库用 maven 坐标。
    /// </summary>
    private static async Task<string> WriteVersionJsonAsync(
        string mcDir,
        string versionId,
        string parentVersion,
        string optiFineTag,
        string launchWrapperVersion,
        CancellationToken ct)
    {
        var root = new JsonObject
        {
            ["id"] = versionId,
            ["inheritsFrom"] = parentVersion,
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["type"] = "release",
            ["releaseTime"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["time"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray("--tweakClass", "optifine.OptiFineTweaker"),
                ["jvm"] = new JsonArray(),
            },
            ["libraries"] = new JsonArray
            {
                new JsonObject { ["name"] = $"optifine:OptiFine:{optiFineTag}" },
                new JsonObject { ["name"] = $"optifine:launchwrapper-of:{launchWrapperVersion}" },
            },
        };

        var versionDir = Path.Combine(mcDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        var path = Path.Combine(versionDir, $"{versionId}.json");
        var tempPath = path + ".tmp";

        await File.WriteAllTextAsync(tempPath, root.ToJsonString(JsonDefaults.Write), ct).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);

        return path;
    }

    private static void EnsureParentInstalled(string mcDir, string gameVersion)
    {
        var parentJson = Path.Combine(mcDir, "versions", gameVersion, $"{gameVersion}.json");

        if (!File.Exists(parentJson))
        {
            throw new InvalidOperationException(
                $"父版本 {gameVersion} 未安装（找不到 {parentJson}）。" +
                $"请先执行：install {gameVersion} --dir \"{mcDir}\"");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响结果
        }
    }
}
