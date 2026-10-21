using System.Text.Json;
using System.Text.Json.Serialization;

namespace McInstaller.App.Services;

/// <summary>应用全局配置</summary>
public sealed class AppConfig
{
    /// <summary>当前使用的 .minecraft 目录</summary>
    [JsonPropertyName("minecraftDirectory")]
    public string? MinecraftDirectory { get; set; }

    /// <summary>目录历史</summary>
    [JsonPropertyName("knownDirectories")]
    public List<string> KnownDirectories { get; set; } = [];

    /// <summary>上次选中的游戏版本</summary>
    [JsonPropertyName("lastVersionId")]
    public string? LastVersionId { get; set; }

    /// <summary>下载并发数</summary>
    [JsonPropertyName("concurrency")]
    public int Concurrency { get; set; } = 24;

    /// <summary>主题：0 = 跟随系统（默认），1 = 亮色，2 = 暗色。</summary>
    [JsonPropertyName("themeMode")]
    public int ThemeMode { get; set; }

    /// <summary>
    /// 镜像加速总开关
    /// 关闭时全程走官方源
    /// </summary>
    [JsonPropertyName("mirrorEnabled")]
    public bool MirrorEnabled { get; set; } = true;

    /// <summary>
    /// 智能切换
    /// 仅在 MirrorEnabled 为 true 时生效
    /// </summary>
    [JsonPropertyName("autoSwitchMirror")]
    public bool AutoSwitchMirror { get; set; } = true;

    /// <summary>
    /// 慢速阈值（KB/s）
    /// </summary>
    [JsonPropertyName("slowSpeedThresholdKBps")]
    public double SlowSpeedThresholdKBps { get; set; } = 200;

    /// <summary>日志面板开关</summary>
    [JsonPropertyName("showLogPanel")]
    public bool ShowLogPanel { get; set; } = true;

    /// <summary>日志面板高度</summary>
    [JsonPropertyName("logPanelHeightPx")]
    public double LogPanelHeightPx { get; set; } = 240;

    /// <summary>左侧列表宽度</summary>
    [JsonPropertyName("leftPanelWidthPx")]
    public double LeftPanelWidthPx { get; set; } = 340;
    /// <summary>最大重试次数</summary>
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; set; } = 4;

    /// <summary>启动时自动刷新</summary>
    [JsonPropertyName("autoRefreshOnStartup")]
    public bool AutoRefreshOnStartup { get; set; } = true;

    /// <summary>安装完成后自动打开目标 .minecraft 目录</summary>
    [JsonPropertyName("openFolderAfterInstall")]
    public bool OpenFolderAfterInstall { get; set; }
    /// <summary>HTTP 请求日志</summary>
    [JsonPropertyName("networkLogEnabled")]
    public bool NetworkLogEnabled { get; set; }
    /// <summary>
    /// 用户是否已同意用户协议
    /// </summary>
    [JsonPropertyName("agreementAccepted")]
    public bool AgreementAccepted { get; set; }

    /// <summary>启动时自动检查 GitHub 是否有新版本。</summary>
    [JsonPropertyName("checkUpdateOnStartup")]
    public bool CheckUpdateOnStartup { get; set; } = true;
}

/// <summary>
/// 配置读写
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DownLoad2MC");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
            }
        }
        catch
        {
            // 配置损坏时退回默认值
        }

        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Options));
        }
        catch
        {
            // 保存失败不影响当前会话
        }
    }
}
