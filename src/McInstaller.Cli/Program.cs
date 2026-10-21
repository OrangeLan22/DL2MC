using System.Text;
using McInstaller.Cli;
using McInstaller.Core;
using McInstaller.Core.Models;
using McInstaller.Core.Net;
using McInstaller.Core.Pipeline;
using McInstaller.Core.Sources;
using Serilog;

Console.OutputEncoding = Encoding.UTF8;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        ConfigureLogging();

        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var command = args[0].ToLowerInvariant();
            var rest = args[1..];

            return command switch
            {
                "list" => await ListAsync(rest),
                "info" => await InfoAsync(rest),
                "install" => await InstallAsync(rest),
                "fabric" => await FabricAsync(rest),
                "fabric-loaders" => await FabricLoadersAsync(rest),
                "forge" => await MavenLoaderAsync(rest, "forge"),
                "neoforge" => await MavenLoaderAsync(rest, "neoforge"),
                "optifine" => await OptiFineAsync(rest),
                "mod" => await ModAsync(rest),
                "-h" or "--help" or "help" => PrintUsage(),
                _ => Unknown(command),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("\n已取消。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n错误：{ex.Message}");
            Log.Error(ex, "未处理异常");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    // ── list ──────────────────────────────────────────────

    private static async Task<int> ListAsync(string[] args)
    {
        var (source, _) = BuildSource(args);
        var includeSnapshot = !args.Contains("--release-only");
        var limit = ParseIntOption(args, "--limit", 30);

        using var http = new HttpFetcher();
        var resolver = new VersionResolver(http, source);
        var manifest = await resolver.GetManifestAsync();

        Console.WriteLine($"源：{source.Name}");
        Console.WriteLine($"最新正式版：{manifest.Latest.Release}    最新快照：{manifest.Latest.Snapshot}");
        Console.WriteLine();

        var versions = manifest.Versions
            .Where(v => includeSnapshot || v.Type == "release")
            .Take(limit);

        Console.WriteLine($"{"版本",-24} {"类型",-10} {"发布时间",-20}");
        Console.WriteLine(new string('-', 58));

        foreach (var v in versions)
        {
            Console.WriteLine($"{v.Id,-24} {v.Type,-10} {v.ReleaseTime:yyyy-MM-dd HH:mm}");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {manifest.Versions.Count} 个版本（显示前 {Math.Min(limit, manifest.Versions.Count)} 个）");
        return 0;
    }

    // ── info ──────────────────────────────────────────────

    private static async Task<int> InfoAsync(string[] args)
    {
        var versionId = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new ArgumentException("缺少版本号");

        var (source, rest) = BuildSource(args);
        var platform = PlatformInfo.Current;

        using var http = new HttpFetcher();
        var resolver = new VersionResolver(http, source);

        var summary = await resolver.FindVersionAsync(versionId)
            ?? throw new InvalidOperationException($"版本清单中不存在：{versionId}");

        var (detail, _) = await resolver.GetDetailAsync(summary);
        var libraryResolver = new LibraryResolver(platform);
        var jobs = libraryResolver.Resolve(detail, source);

        var client = detail.Downloads?.Client;

        Console.WriteLine($"版本        : {detail.Id}");
        Console.WriteLine($"类型        : {detail.Type ?? summary.Type}");
        Console.WriteLine($"发布时间    : {summary.ReleaseTime:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"mainClass   : {detail.MainClass}");
        Console.WriteLine($"继承自      : {detail.InheritsFrom ?? "(无)"}");
        Console.WriteLine($"要求 Java   : {(detail.JavaVersion?.MajorVersion.ToString() ?? "未声明")}");
        Console.WriteLine();
        Console.WriteLine($"客户端 jar  : {(client?.Size is { } cs ? ConsoleProgress.FormatBytes(cs) : "无")}");
        Console.WriteLine($"库文件      : {detail.Libraries.Count} 个声明，{jobs.Count} 个适用于当前平台 ({platform.OsName}/{platform.OsArch})");
        Console.WriteLine($"库文件总计  : {ConsoleProgress.FormatBytes(jobs.Sum(j => j.Size ?? 0))}");

        if (detail.AssetIndex is { } index)
        {
            Console.WriteLine();
            Console.WriteLine($"资源索引    : {index.Id}");
            Console.WriteLine($"资源总量    : {(index.TotalSize is { } ts ? ConsoleProgress.FormatBytes(ts) : "未知")}");
            Console.WriteLine($"虚拟资源    : {(index.Virtual ? "是（需展开）" : "否")}");
            Console.WriteLine($"映射resources: {(index.MapToResources ? "是" : "否")}");
        }

        return 0;
    }

    // ── install ───────────────────────────────────────────

    private static async Task<int> InstallAsync(string[] args)
    {
        var versionId = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new ArgumentException("缺少版本号");

        var directory = ParseStringOption(args, "--dir")
            ?? throw new ArgumentException("缺少 --dir 参数（目标 .minecraft 目录）");

        var (source, _) = BuildSource(args);
        var concurrency = ParseIntOption(args, "--concurrency", 24);

        Console.WriteLine($"版本    : {versionId}");
        Console.WriteLine($"目录    : {Path.GetFullPath(directory)}");
        Console.WriteLine($"数据源  : {source.Name}");
        Console.WriteLine($"并发数  : {concurrency}");
        Console.WriteLine();

        Log.Information("开始安装 {Version} 到 {Dir}，源={Source}，并发={Concurrency}",
            versionId, directory, source.Name, concurrency);

        using var http = new HttpFetcher(maxConnections: concurrency);
        var installer = new Installer(http, source, concurrency: concurrency);
        var progress = new ConsoleProgress();

        var result = await installer.InstallAsync(
            new InstallRequest(versionId, directory), progress);

        progress.Finish();

        Console.WriteLine();
        Console.WriteLine($"结果      : {(result.Success ? "成功" : "失败")}");
        Console.WriteLine($"已下载    : {result.FilesDownloaded} 个文件，{ConsoleProgress.FormatBytes(result.BytesDownloaded)}");
        Console.WriteLine($"已跳过    : {result.FilesSkipped} 个（校验通过）");
        Console.WriteLine($"失败      : {result.FilesFailed} 个");

        if (result.VersionJsonPath is { } jsonPath)
        {
            Console.WriteLine($"版本 JSON : {jsonPath}");
        }

        if (result.Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败清单：");
            foreach (var f in result.Failures.Take(20))
            {
                Console.WriteLine($"  {f.Job.DisplayName}: {f.Error}");
            }

            if (result.Failures.Count > 20)
            {
                Console.WriteLine($"  ...（共 {result.Failures.Count} 条）");
            }
        }

        Log.Information("安装结束：{Result}", result);

        return result.Success ? 0 : 1;
    }

    // ── fabric ────────────────────────────────────────────

    private static async Task<int> FabricAsync(string[] args)
    {
        var gameVersion = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new ArgumentException("缺少父版本号，例如：fabric 1.21.11 --dir ...");

        var directory = ParseStringOption(args, "--dir")
            ?? throw new ArgumentException("缺少 --dir 参数（目标 .minecraft 目录）");

        var loaderVersion = ParseStringOption(args, "--loader");
        var (source, _) = BuildSource(args);
        var concurrency = ParseIntOption(args, "--concurrency", 24);

        Console.WriteLine($"加载器  : Fabric");
        Console.WriteLine($"父版本  : {gameVersion}");
        Console.WriteLine($"Loader  : {loaderVersion ?? "(自动选 stable)"}");
        Console.WriteLine($"目录    : {Path.GetFullPath(directory)}");
        Console.WriteLine($"数据源  : {source.Name}");
        Console.WriteLine();

        Log.Information("开始安装 Fabric 到 {Dir}，父版本={Parent}，loader={Loader}",
            directory, gameVersion, loaderVersion ?? "auto");

        using var http = new HttpFetcher(maxConnections: concurrency);
        var installer = new McInstaller.Core.Loaders.FabricInstaller(http, source, concurrency: concurrency);
        var progress = new ConsoleProgress();

        var result = await installer.InstallAsync(gameVersion, loaderVersion, directory, progress);

        progress.Finish();

        Console.WriteLine();
        Console.WriteLine($"结果      : {(result.Success ? "成功" : "失败")}");
        Console.WriteLine($"版本 ID   : {result.VersionId}");
        Console.WriteLine($"Loader    : {result.LoaderVersion}");
        Console.WriteLine($"继承自    : {result.ParentVersion}");
        Console.WriteLine($"已下载    : {result.FilesDownloaded} 个，{ConsoleProgress.FormatBytes(result.BytesDownloaded)}");
        Console.WriteLine($"已跳过    : {result.FilesSkipped} 个");
        Console.WriteLine($"版本 JSON : {result.VersionJsonPath}");

        if (result.Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败清单：");
            foreach (var f in result.Failures.Take(20))
            {
                Console.WriteLine($"  {f.Job.DisplayName}: {f.Error}");
            }
        }

        Log.Information("Fabric 安装结束：{Result}", result);
        return result.Success ? 0 : 1;
    }

    private static async Task<int> FabricLoadersAsync(string[] args)
    {
        using var http = new HttpFetcher();
        var meta = new McInstaller.Core.Loaders.FabricMetaClient(http);

        // 指定游戏版本时，走「该版本可用的 loader」端点（列表会短很多）
        var game = ParseStringOption(args, "--game");

        if (!string.IsNullOrWhiteSpace(game))
        {
            var forGame = await meta.GetLoadersForGameAsync(game);
            Console.WriteLine($"Minecraft {game} 可用的 Fabric loader 共 {forGame.Count} 个：");
            Console.WriteLine();

            foreach (var l in forGame.Take(20))
            {
                Console.WriteLine($"  {l.Version,-12} {(l.Stable ? "stable" : "")}");
            }

            if (forGame.Count > 20)
            {
                Console.WriteLine($"  …共 {forGame.Count} 个");
            }

            return 0;
        }

        var loaders = await meta.GetLoaderVersionsAsync();

        var showAll = args.Contains("--all");
        var list = showAll ? loaders : loaders.Where(l => l.Stable).ToList();

        Console.WriteLine(showAll
            ? $"Fabric loader 共 {loaders.Count} 个版本："
            : $"Fabric loader 稳定版共 {list.Count} 个（加 --all 看全部 {loaders.Count} 个）：");
        Console.WriteLine();

        foreach (var l in list.Take(40))
        {
            Console.WriteLine($"  {l.Version,-12} {(l.Stable ? "stable" : "")}");
        }

        return 0;
    }

    // ── forge / neoforge ──────────────────────────────────

    /// <summary>
    /// Forge 与 NeoForge 的安装流程完全同构（maven 查版本 + 调官方 installer），
    /// 差异封在 IMavenLoaderSource 里，因此两者共用一个入口。
    /// </summary>
    private static async Task<int> MavenLoaderAsync(string[] args, string kind)
    {
        var gameVersion = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new ArgumentException($"缺少父版本号，例如：{kind} 1.21.11 --dir ...");

        var directory = ParseStringOption(args, "--dir")
            ?? throw new ArgumentException("缺少 --dir 参数（目标 .minecraft 目录）");

        var loaderVersion = ParseStringOption(args, "--loader");
        var (_, _) = BuildSource(args);

        using var http = new HttpFetcher();

        McInstaller.Core.Loaders.IMavenLoaderSource source = kind switch
        {
            "forge" => new McInstaller.Core.Loaders.ForgeMavenSource(http),
            "neoforge" => new McInstaller.Core.Loaders.NeoForgeMavenSource(http),
            _ => throw new ArgumentException($"未知的加载器：{kind}"),
        };

        Console.WriteLine($"加载器  : {source.LoaderType}");
        Console.WriteLine($"父版本  : {gameVersion}");
        Console.WriteLine($"版本    : {loaderVersion ?? "(自动选最新)"}");
        Console.WriteLine($"目录    : {Path.GetFullPath(directory)}");
        Console.WriteLine();

        Log.Information("开始安装 {Loader} 到 {Dir}，父版本={Parent}，版本={Version}",
            source.LoaderType, directory, gameVersion, loaderVersion ?? "auto");

        var installer = new McInstaller.Core.Loaders.MavenLoaderInstaller(
            source, http, log: msg => Log.Information("{Message}", msg));

        var progress = new ConsoleProgress();
        var result = await installer.InstallAsync(gameVersion, loaderVersion, directory, progress);

        progress.Finish();

        Console.WriteLine();
        Console.WriteLine($"结果      : {(result.Success ? "成功" : "失败")}");
        Console.WriteLine($"版本 ID   : {result.VersionId}");
        Console.WriteLine($"{result.LoaderType,-10}: {result.LoaderVersion}");
        Console.WriteLine($"继承自    : {result.ParentVersion}");
        Console.WriteLine($"版本 JSON : {result.VersionJsonPath}");

        Log.Information("{Loader} 安装结束：{Result}", result.LoaderType, result);
        return result.Success ? 0 : 1;
    }

    // ── optifine ──────────────────────────────────────────

    private static async Task<int> OptiFineAsync(string[] args)
    {
        var gameVersion = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new ArgumentException("缺少父版本号，例如：optifine 1.21.11 --dir ...");

        var directory = ParseStringOption(args, "--dir")
            ?? throw new ArgumentException("缺少 --dir 参数（目标 .minecraft 目录）");

        var loaderVersion = ParseStringOption(args, "--loader");

        Console.WriteLine($"加载器  : OptiFine");
        Console.WriteLine($"父版本  : {gameVersion}");
        Console.WriteLine($"版本    : {loaderVersion ?? "(自动选最新正式版)"}");
        Console.WriteLine($"目录    : {Path.GetFullPath(directory)}");
        Console.WriteLine($"数据源  : BMCLAPI");
        Console.WriteLine();

        Log.Information("开始安装 OptiFine 到 {Dir}，父版本={Parent}，版本={Version}",
            directory, gameVersion, loaderVersion ?? "auto");

        using var http = new HttpFetcher();
        var installer = new McInstaller.Core.Loaders.OptiFineInstaller(
            http, log: msg => Log.Information("{Message}", msg));

        var progress = new ConsoleProgress();
        var result = await installer.InstallAsync(gameVersion, loaderVersion, directory, progress);

        progress.Finish();

        Console.WriteLine();
        Console.WriteLine($"结果      : {(result.Success ? "成功" : "失败")}");
        Console.WriteLine($"版本 ID   : {result.VersionId}");
        Console.WriteLine($"OptiFine  : {result.LoaderVersion}");
        Console.WriteLine($"继承自    : {result.ParentVersion}");
        Console.WriteLine($"版本 JSON : {result.VersionJsonPath}");

        Log.Information("OptiFine 安装结束：{Result}", result);
        return result.Success ? 0 : 1;
    }

    // ── mod ─────────────────────────────────────────────────

    private static async Task<int> ModAsync(string[] args)
    {
        var slug = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new ArgumentException("缺少 Modrinth 项目 slug，例如：mod fabric-api --mc 1.21.11 --dir ...");

        var mcVersion = ParseStringOption(args, "--mc")
            ?? throw new ArgumentException("缺少 --mc 参数（游戏版本）");

        var directory = ParseStringOption(args, "--dir")
            ?? throw new ArgumentException("缺少 --dir 参数（目标 .minecraft 目录）");

        var loader = ParseStringOption(args, "--loader") ?? "fabric";
        var modVersion = ParseStringOption(args, "--mod-version") ?? ParseStringOption(args, "--version");
        var target = ParseStringOption(args, "--target");

        Console.WriteLine($"项目    : {slug}");
        Console.WriteLine($"游戏版本: {mcVersion}");
        Console.WriteLine($"加载器  : {loader}");
        Console.WriteLine($"目录    : {Path.GetFullPath(directory)}");
        Console.WriteLine($"目标版本: {target ?? "(自动探测隔离目录)"}");
        Console.WriteLine();

        using var http = new HttpFetcher();
        var installer = new McInstaller.Core.Mods.ModInstaller(http);
        var progress = new ConsoleProgress();

        var result = await installer.InstallAsync(
            slug, mcVersion, loader, directory, progress, modVersion, target);

        progress.Finish();

        Console.WriteLine();
        Console.WriteLine($"结果      : {(result.Success ? "成功" : "失败")}");
        Console.WriteLine($"项目名称  : {result.Title}");
        Console.WriteLine($"mod 版本  : {result.ModVersion}");
        Console.WriteLine($"文件      : {result.FileName}");
        Console.WriteLine($"安装位置  : {result.TargetPath}");

        if (result.UsedVersionIsolation)
        {
            Console.WriteLine($"版本隔离  : 是（写入 {result.ResolvedVersionId} 的隔离目录）");
        }
        else
        {
            Console.WriteLine("版本隔离  : 未检测到 —— 写入 .minecraft/mods/");
            Console.WriteLine("            若启动器开启了版本隔离，这个文件不会被加载");
            Console.WriteLine("            此时请用 --target <版本 id> 指定目标版本");
        }

        Console.WriteLine($"状态      : {(result.Skipped ? "已存在（校验通过，跳过）" : $"新下载 {ConsoleProgress.FormatBytes(result.Bytes)}")}");

        return result.Success ? 0 : 1;
    }

    // ── 辅助 ──────────────────────────────────────────────

    private static (IDownloadSource Source, string[] Remaining) BuildSource(string[] args)
    {
        var useMirror = args.Contains("--mirror");
        IDownloadSource source = useMirror ? new BmclapiSource() : new MojangSource();

        var remaining = args.Where(a => a != "--mirror").ToArray();
        return (source, remaining);
    }

    private static int ParseIntOption(string[] args, string name, int fallback)
    {
        var value = ParseStringOption(args, name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string? ParseStringOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index >= 0 && index + 1 < args.Length)
        {
            return args[index + 1];
        }

        return null;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 1;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            DownLoad2MC — 原版 Minecraft 资源下载器

            用法：
              DownLoad2MC list   [--limit N] [--release-only] [--mirror]
              DownLoad2MC info   <版本号> [--mirror]
              DownLoad2MC install <版本号> --dir <目标 .minecraft 目录> [--concurrency N] [--mirror]
              DownLoad2MC fabric  <父版本号> --dir <目录> [--loader <版本>] [--mirror]
              DownLoad2MC fabric-loaders [--all]
              DownLoad2MC forge   <父版本号> --dir <目录> [--loader <forge版本>]
              DownLoad2MC neoforge <父版本号> --dir <目录> [--loader <neoforge版本>]
              DownLoad2MC optifine <父版本号> --dir <目录> [--loader <HD_U_J9>]
              DownLoad2MC mod     <slug> --mc <游戏版本> --dir <目录> [--loader fabric|forge|neoforge]
                                    [--mod-version <版本>] [--target <隔离版本 id>]

            说明：
              --mirror       使用 BMCLAPI 镜像（官方源仍是回退终点）
              --concurrency  并发连接数，默认 24
              --release-only 仅显示正式版
              --loader       指定 Fabric loader 版本，缺省选最新 stable
              --target       指定 mod 写入哪个版本的隔离目录（启动器开了版本隔离时必须对）
              --mod-version  指定 mod 自身的版本，缺省选最新 release

            示例：
              DownLoad2MC list --limit 10
              DownLoad2MC info 1.21.11
              DownLoad2MC install 1.21.11 --dir "C:\Users\You\AppData\Roaming\.minecraft"
              DownLoad2MC fabric 1.21.11 --dir "C:\Users\You\AppData\Roaming\.minecraft"
              DownLoad2MC mod fabric-api --mc 1.21.11 --dir "C:\Users\You\AppData\Roaming\.minecraft"
              DownLoad2MC forge 1.20.1 --dir "C:\Users\You\AppData\Roaming\.minecraft"
              DownLoad2MC neoforge 1.21.11 --dir "C:\Users\You\AppData\Roaming\.minecraft"
              DownLoad2MC optifine 1.21.11 --dir "C:\Users\You\AppData\Roaming\.minecraft"
            """);
        return 0;
    }

    private static void ConfigureLogging()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "downlog2mc-.log"),
                rollingInterval: RollingInterval.Day,
                // 带 BOM 的 UTF-8：否则 Windows 记事本会按 GBK 解读，中文日志全是乱码
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
