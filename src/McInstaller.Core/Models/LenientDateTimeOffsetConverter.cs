using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McInstaller.Core.Models;

/// <summary>
/// 宽容的 DateTimeOffset 转换器。
///
/// 起因：Fabric 的 profile 把时区写成 <c>+0000</c>（无冒号），
/// 而 System.Text.Json 的内置转换器只接受 <c>+00:00</c>，直接抛异常导致整个版本解析失败。
///
/// 原则（见 docs/DESIGN.md §10 风险清单）：解析层对非关键字段做降级，不要因为
/// 一个展示用字段让整个安装流程崩掉。
/// </summary>
public sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 标准与宽松格式都试一遍；两者都失败时返回 null 而不是抛异常
        if (DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        if (DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            return parsed;
        }

        return null;
    }

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // 统一输出带冒号的 ISO 8601
        writer.WriteStringValue(value.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
    }
}
