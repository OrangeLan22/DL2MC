using System.Text.Json;

namespace McInstaller.Core;

/// <summary>
/// 统一的 JSON 序列化选项。
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// 属性名大小写不敏感 + 允许注释/尾随逗号。
    /// Mojang 与各加载器源的响应格式历史上都出现过小写/驼峰混用，容忍比严格更实用。
    /// </summary>
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>写出用：缩进，便于人工核对落盘的版本 JSON。</summary>
    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Read)
           ?? throw new InvalidOperationException($"反序列化 {typeof(T).Name} 得到 null");
}
