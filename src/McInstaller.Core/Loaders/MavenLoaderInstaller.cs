using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using McInstaller.Core.Java;
using McInstaller.Core.Models;
using McInstaller.Core.Net;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Util;
using McInstaller.Core.Progress;

namespace McInstaller.Core.Loaders;

/// <summary>
/// 基于 maven + 官方 installer 的加载器安装器，Forge 与 NeoForge 共用。
///
/// 为什么必须调用 installer 而不能手工安装：
/// 实测 Forge installer jar 内含 373 个条目，其中 install_profile.json 带 processors 工具链、
/// data/client.lzma 是需要 binarypatcher 处理的二进制补丁。手工复刻等于重写一遍 installer。
///
/// 由此必须处理的三个点：
///   1. 需要可用的 JVM（JavaLocator 探测）
///   2. installer 会自行联网拉 maven 依赖，慢时长时间无输出（超时 + 流式日志）
///   3. 失败可能留下半安装目录（安装前后对比 + 只认新出现的目录）
/// </summary>
public sealed class MavenLoaderInstaller : ILoaderInstaller
{
    private readonly IMavenLoaderSource _source;
    private readonly HttpFetcher _http;
    private readonly ProcessRunner _runner;
    private readonly JavaLocator _java;
    private readonly ConcurrentDownloader _downloader;
    private readonly LibraryResolver _libraries;
    private readonly Action<string>? _log;

    public MavenLoaderInstaller(
        IMavenLoaderSource source,
        HttpFetcher http,
        ProcessRunner? runner = null,
        Action<string>? log = null)
    {
        _source = source;
        _http = http;
        _runner = runner ?? new ProcessRunner();
        _java = new JavaLocator(_runner);
        _downloader = new ConcurrentDownloader(http, concurrency: 4);
        _libraries = new LibraryResolver(PlatformInfo.Current);
        _log = log;
    }

    public string Name => _source.LoaderType;

    public async Task<LoaderInstallResult> InstallAsync(
        string gameVersion,
        string? loaderVersion,
        string minecraftDirectory,
        IDownloadProgress? progress = null,
        CancellationToken ct = default)
    {
        progress ??= NullProgress.Instance;
        var mcDir = Path.GetFullPath(minecraftDirectory);
        var loaderType = _source.LoaderType;

        EnsureParentInstalled(mcDir, gameVersion, loaderType);

        var candidates = await _source.GetLoaderVersionsAsync(gameVersion, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"{loaderType} 没有适配 Minecraft {gameVersion} 的版本。" +
                "该游戏版本可能太新，加载器尚未跟进。");
        }

        var selected = loaderVersion ?? candidates[0];
        var fullVersion = _source.ToFullVersion(gameVersion, selected);

        _log?.Invoke($"{loaderType} 版本：{fullVersion}（共 {candidates.Count} 个可选）");

        // 需要哪个 Java —— 直接读父版本 JSON，不做版本号映射表
        var requiredJava = ReadRequiredJava(mcDir, gameVersion);
        _log?.Invoke($"该游戏版本要求 Java {requiredJava}+");

        var java = await _java.FindAsync(requiredJava, ct: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"未找到 Java {requiredJava} 或更高版本，无法运行 {loaderType} installer。\n" +
                $"请安装 Java {requiredJava}+ 后重试。\n" +
                "已探测：JAVA_HOME、PATH、Program Files 下的常见 JDK 目录。");

        _log?.Invoke($"使用 Java：{java.Path}（{java.FullVersion}，来源 {java.Source}）");

        var tempDir = Path.Combine(Path.GetTempPath(), "downlog2mc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var installerName = $"{loaderType.ToLowerInvariant()}-{fullVersion}-installer.jar";
            var installerPath = Path.Combine(tempDir, installerName);
            var installerUri = _source.GetInstallerUri(fullVersion);

            _log?.Invoke($"[下载] {loaderType} installer：{installerUri}");

            // 走统一的下载器，才能把 installer 文件本身列进进度窗口
            var installerJob = new DownloadJob(installerName, installerUri, null, null, installerName);
            var downloadResult = await _downloader
                .RunAsync([installerJob], tempDir, progress, ct)
                .ConfigureAwait(false);

            if (!downloadResult.Success)
            {
                var reason = downloadResult.Failures.FirstOrDefault()?.Error ?? "未知错误";
                throw new InvalidOperationException($"{loaderType} installer 下载失败：{reason}");
            }

            var installerBytes = new FileInfo(installerPath).Length;
            _log?.Invoke($"[下载] installer 完成：{installerBytes / 1024} KB");

            // NeoForge installer 不支持 --mirror，它自己会直连官方 maven 下载依赖，
            // 国内网络下就是 Connect timed out。这里先把这些库抓到本地，
            // installer 见到文件已存在就会跳过联网。
            await PrefetchInstallerLibrariesAsync(
                installerPath,
                mcDir,
                useMirror: installerUri.Host.Contains("bangbang93", StringComparison.OrdinalIgnoreCase),
                progress,
                ct).ConfigureAwait(false);

            // 安装前的版本目录快照，用于事后定位 installer 生成的目录
            var before = SnapshotVersions(mcDir);

            _log?.Invoke($"执行：{java.Path} -jar {installerPath} --installClient {mcDir}");
            // installer 由子进程执行，无法获得内部进度，只能提示当前阶段
            _log?.Invoke($"{loaderType} installer 正在执行，此阶段无下载进度，请耐心等待…");
            var processResult = await _runner.RunAsync(
                java.Path,
                ["-jar", installerPath, "--installClient", mcDir],
                workingDirectory: mcDir,
                timeout: TimeSpan.FromMinutes(20),
                onOutputLine: line =>
                {
                    // installer 会把每个 library 的类路径都打出来，几千行同质输出
                    // 会淹没 UI 线程，这里只留阶段推进与报错
                    if (IsSignificantLine(line))
                    {
                        _log?.Invoke($"[安装] {line}");
                    }
                },
                ct: ct).ConfigureAwait(false);

            if (processResult.TimedOut)
            {
                throw new InvalidOperationException(
                    $"{loaderType} installer 超时（20 分钟）。常见原因是无法访问 maven 依赖源，\n" +
                    "请检查网络后重试。");
            }

            if (processResult.ExitCode != 0)
            {
                // 老版本 Forge（1.8.9 等）的 installer 没有 --installClient 参数，
                // 但它自带 install_profile.json（含完整 versionInfo）与 universal jar，
                // 可以据此手工完成安装。
                if (IsUnsupportedOption(processResult))
                {
                    _log?.Invoke($"[安装] 该版本 {loaderType} installer 不支持 --installClient，改用手工安装");

                    var legacyId = await InstallLegacyAsync(installerPath, mcDir, progress, ct)
                        .ConfigureAwait(false);

                    _log?.Invoke($"[安装] {loaderType} 手工安装完成：{legacyId}");

                    return new LoaderInstallResult(
                        LoaderType: loaderType,
                        VersionId: legacyId,
                        ParentVersion: gameVersion,
                        LoaderVersion: selected,
                        Success: true,
                        VersionJsonPath: Path.Combine(mcDir, "versions", legacyId, $"{legacyId}.json"),
                        FilesDownloaded: 1,
                        FilesSkipped: 0,
                        FilesFailed: 0,
                        BytesDownloaded: new FileInfo(installerPath).Length,
                        Failures: []);
                }

                var tail = Tail(processResult.StandardError, 30);
                throw new InvalidOperationException(
                    $"{loaderType} installer 失败（退出码 {processResult.ExitCode}）。\n{tail}");
            }

            // 定位 installer 生成的版本目录
            var after = SnapshotVersions(mcDir);
            var created = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();

            var versionId = created.FirstOrDefault()
                ?? after.FirstOrDefault(v => v.Contains(selected, StringComparison.OrdinalIgnoreCase));

            if (versionId is null)
            {
                throw new InvalidOperationException(
                    $"{loaderType} installer 报告成功，但 versions/ 下没有找到新生成的版本目录。\n" +
                    $"安装前的目录：{string.Join(", ", before)}");
            }

            var jsonPath = Path.Combine(mcDir, "versions", versionId, $"{versionId}.json");
            if (!File.Exists(jsonPath))
            {
                throw new InvalidOperationException($"生成的版本目录缺少 {versionId}.json：{jsonPath}");
            }

            _log?.Invoke($"{loaderType} 安装完成，版本目录：{versionId}");

            return new LoaderInstallResult(
                LoaderType: loaderType,
                VersionId: versionId,
                ParentVersion: gameVersion,
                LoaderVersion: selected,
                Success: true,
                VersionJsonPath: jsonPath,
                FilesDownloaded: 1,
                FilesSkipped: 0,
                FilesFailed: 0,
                BytesDownloaded: new FileInfo(installerPath).Length,
                Failures: []);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// 判断 installer 的某行输出是否值得记录。
    /// Forge/NeoForge 会逐条打印 library 路径与类名，全量记录会让日志刷屏。
    /// </summary>
    private static bool IsSignificantLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var lower = line.ToLowerInvariant();

        if (lower.Contains("error") || lower.Contains("warning") ||
            lower.Contains("fail") || lower.Contains("exception") ||
            lower.Contains("caused by"))
        {
            return true;
        }

        return lower.Contains("downloading") || lower.Contains("extracting") ||
               lower.Contains("installing") || lower.Contains("patching") ||
               lower.Contains("completed") || lower.Contains("successfully") ||
               lower.Contains("processing") || lower.Contains("injecting");
    }
    /// <summary>
    /// 把 installer 自己要联网下载的库提前抓到本地，installer 运行时就会跳过下载。
    /// 依赖清单来自 installer jar 内的 install_profile.json。
    /// </summary>
    private async Task PrefetchInstallerLibrariesAsync(
        string installerPath,
        string mcDir,
        bool useMirror,
        IDownloadProgress progress,
        CancellationToken ct)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(installerPath);
        }
        catch
        {
            return;   // 不是合法 jar，交给 installer 自己报错
        }

        using (zip)
        {
            var entry = zip.GetEntry("install_profile.json");
            if (entry is null)
            {
                return;
            }

            using var stream = entry.Open();
            using var doc = await JsonDocument
                .ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
            {
                return;
            }

            var librariesDir = Path.Combine(mcDir, "libraries");

            // 镜像优先；没开镜像时分别落到各自官方 maven
            var baseUrl = useMirror
                ? "https://bmclapi2.bangbang93.com/maven/"
                : _source.LoaderType == "neoforge"
                    ? "https://maven.neoforged.net/releases/"
                    : "https://maven.minecraftforge.net/";

            var jobs = new List<DownloadJob>();

            foreach (var library in libraries.EnumerateArray())
            {
                if (!library.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                MavenCoordinate coordinate;
                try
                {
                    coordinate = MavenCoordinate.Parse(name);
                }
                catch
                {
                    continue;   // 坐标异常就跳过，别让预下载拖垮整个安装
                }

                // install_profile 里通常带 downloads.artifact，含权威 url 与 sha1。
                // 用它来构造任务：镜像作首选源，官方 url 作回退，
                // 这样即使镜像缺这个库也能拿到正确的文件并校验。
                string relativePath;
                Uri primaryUri;
                Uri? fallbackUri = null;
                string? sha1 = null;
                long? size = null;

                if (library.TryGetProperty("downloads", out var downloads) &&
                    downloads.TryGetProperty("artifact", out var artifact) &&
                    artifact.TryGetProperty("path", out var pathElement) &&
                    pathElement.GetString() is { Length: > 0 } declaredPath)
                {
                    relativePath = declaredPath;

                    if (artifact.TryGetProperty("url", out var urlElement) &&
                        urlElement.GetString() is { Length: > 0 } declaredUrl &&
                        Uri.TryCreate(declaredUrl, UriKind.Absolute, out var officialUri))
                    {
                        fallbackUri = officialUri;
                    }

                    if (artifact.TryGetProperty("sha1", out var sha1Element))
                    {
                        sha1 = sha1Element.GetString();
                    }

                    if (artifact.TryGetProperty("size", out var sizeElement) &&
                        sizeElement.TryGetInt64(out var declaredSize))
                    {
                        size = declaredSize;
                    }

                    primaryUri = new Uri(baseUrl + relativePath);
                }
                else
                {
                    // 没有 artifact 信息，只能按坐标拼路径
                    relativePath = coordinate.ToRelativePath();
                    primaryUri = new Uri(baseUrl + relativePath);
                }

                // 已经有了就跳过，省一次请求
                if (File.Exists(Path.Combine(librariesDir, relativePath)))
                {
                    continue;
                }

                jobs.Add(new DownloadJob(
                    relativePath,
                    primaryUri,
                    sha1,
                    size,
                    name,
                    fallbackUri is null ? null : () => fallbackUri));
            }

            if (jobs.Count == 0)
            {
                _log?.Invoke("[安装] installer 依赖已齐全，跳过预下载");
                return;
            }

            _log?.Invoke($"[安装] 预下载 {jobs.Count} 个 installer 依赖" +
                         (useMirror ? "（走 BMCLAPI 镜像）" : "（官方 maven）"));

            var result = await _downloader
                .RunAsync(jobs, librariesDir, progress, ct)
                .ConfigureAwait(false);

            _log?.Invoke(result.Success
                ? $"[安装] 依赖预下载完成：{result.Downloaded} 个"
                : $"[安装] 依赖预下载有 {result.Failures.Count} 个失败，installer 会尝试自行补齐");
        }
    }
    /// <summary>installer 是否因为不认识 --installClient 而失败（老版本 Forge）。</summary>
    private static bool IsUnsupportedOption(ProcessResult result)
        => result.StandardError.Contains("UnrecognizedOptionException", StringComparison.OrdinalIgnoreCase) ||
           result.StandardError.Contains("is not a recognized option", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 手工安装老版本 Forge。
    ///
    /// 1.8.9 这类老 installer 没有 --installClient，只能弹 GUI；
    /// 但它的 jar 里有 install_profile.json（含完整 versionInfo）与 universal jar，
    /// 照着这两样落盘即可，与 GUI 做的事等价。
    /// </summary>
    private async Task<string> InstallLegacyAsync(
        string installerPath, string mcDir, IDownloadProgress progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(installerPath);

        var profileEntry = zip.GetEntry("install_profile.json")
            ?? throw new InvalidOperationException(
                $"installer 内缺少 install_profile.json，且不支持 --installClient，无法自动安装。\n" +
                "请手动运行 installer 完成安装。");

        string profileJson;
        using (var reader = new StreamReader(profileEntry.Open()))
        {
            profileJson = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var profile = JsonNode.Parse(profileJson)
            ?? throw new InvalidOperationException("install_profile.json 解析失败");

        var versionInfo = profile["versionInfo"]
            ?? throw new InvalidOperationException("install_profile.json 缺少 versionInfo");

        var versionId = versionInfo["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("versionInfo 缺少 id");

        // universal jar → libraries/<maven 路径>
        var forgeCoord = profile["install"]?["path"]?.GetValue<string>();
        var bundledName = profile["install"]?["filePath"]?.GetValue<string>();

        if (!string.IsNullOrEmpty(forgeCoord) && !string.IsNullOrEmpty(bundledName))
        {
            var relativePath = MavenCoordinate.Parse(forgeCoord).ToRelativePath();
            var targetPath = Path.Combine(
                mcDir, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));

            var entry = zip.GetEntry(bundledName);
            if (entry is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
                _log?.Invoke($"[安装] 已释放 Forge 主体：{Path.GetFileName(targetPath)}");
            }
        }

        var versionDir = Path.Combine(mcDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        var jsonPath = Path.Combine(versionDir, $"{versionId}.json");
        await File.WriteAllTextAsync(jsonPath, versionInfo.ToJsonString(JsonDefaults.Write), ct)
            .ConfigureAwait(false);

        var detail = versionInfo.Deserialize<VersionDetail>(JsonDefaults.Read)
            ?? throw new InvalidOperationException("versionInfo 无法解析为版本定义");

        // versionInfo 的库是旧格式（无 url 字段），按默认 maven 解析
        var jobs = _libraries.Resolve(detail, new Sources.MojangSource());
        var download = await _downloader.RunAsync(jobs, mcDir, progress, ct).ConfigureAwait(false);

        if (!download.Success)
        {
            var reason = download.Failures.FirstOrDefault()?.Error ?? "未知错误";
            throw new InvalidOperationException($"{_source.LoaderType} 依赖库下载失败：{reason}");
        }

        return versionId;
    }

    private static void EnsureParentInstalled(string mcDir, string gameVersion, string loaderType)
    {
        var parentJson = Path.Combine(mcDir, "versions", gameVersion, $"{gameVersion}.json");

        if (!File.Exists(parentJson))
        {
            throw new InvalidOperationException(
                $"父版本 {gameVersion} 未安装（找不到 {parentJson}）。" +
                $"请先执行：install {gameVersion} --dir \"{mcDir}\"");
        }
    }

    /// <summary>
    /// 从父版本 JSON 读 Java 主版本要求。
    /// 读不到时用 21 作为保守下限 —— 当前主流模组生态的普遍要求。
    /// </summary>
    private static int ReadRequiredJava(string mcDir, string gameVersion)
    {
        try
        {
            var jsonPath = Path.Combine(mcDir, "versions", gameVersion, $"{gameVersion}.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));

            if (doc.RootElement.TryGetProperty("javaVersion", out var jv) &&
                jv.TryGetProperty("majorVersion", out var major) &&
                major.TryGetInt32(out var value) &&
                value > 0)
            {
                return value;
            }
        }
        catch
        {
            // 解析失败不致命，退回默认值
        }

        return 21;
    }

    private static List<string> SnapshotVersions(string mcDir)
    {
        var versionsDir = Path.Combine(mcDir, "versions");

        if (!Directory.Exists(versionsDir))
        {
            return [];
        }

        return Directory.EnumerateDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(installer 未输出错误信息)";
        }

        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.TakeLast(lines));
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
            // 临时文件清理失败不影响结果
        }
    }
}
