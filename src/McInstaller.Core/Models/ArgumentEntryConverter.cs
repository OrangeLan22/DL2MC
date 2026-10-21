using System.Text.Json;
using System.Text.Json.Serialization;

namespace McInstaller.Core.Models;

/// <summary>
/// arguments 数组元素的转换器。
/// 元素有两种形态：
///   "某个字符串"
///   { "rules": [...], "value": "字符串" 或 ["字符串", ...] }
/// </summary>
public sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ArgumentEntry { Value = reader.GetString() };

            case JsonTokenType.StartObject:
                {
                    var entry = new ArgumentEntry();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            break;
                        }

                        if (reader.TokenType != JsonTokenType.PropertyName)
                        {
                            continue;
                        }

                        var prop = reader.GetString();
                        reader.Read();

                        switch (prop)
                        {
                            case "rules":
                                entry.Rules = JsonSerializer.Deserialize<List<Rule>>(ref reader, options);
                                break;

                            case "value":
                                if (reader.TokenType == JsonTokenType.String)
                                {
                                    entry.Value = reader.GetString();
                                }
                                else if (reader.TokenType == JsonTokenType.StartArray)
                                {
                                    entry.Values = JsonSerializer.Deserialize<List<string>>(ref reader, options);
                                }
                                else
                                {
                                    reader.Skip();
                                }
                                break;

                            default:
                                reader.Skip();
                                break;
                        }
                    }

                    return entry;
                }

            default:
                reader.Skip();
                return new ArgumentEntry();
        }
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
    {
        if (value.Rules is null or { Count: 0 })
        {
            if (value.Values is { Count: > 0 })
            {
                writer.WriteStartArray();
                foreach (var v in value.Values)
                {
                    writer.WriteStringValue(v);
                }
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteStringValue(value.Value ?? "");
            }

            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("rules");
        JsonSerializer.Serialize(writer, value.Rules, options);

        writer.WritePropertyName("value");
        if (value.Values is { Count: > 0 })
        {
            writer.WriteStartArray();
            foreach (var v in value.Values)
            {
                writer.WriteStringValue(v);
            }
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteStringValue(value.Value ?? "");
        }

        writer.WriteEndObject();
    }
}
