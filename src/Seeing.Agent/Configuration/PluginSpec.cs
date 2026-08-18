using System.Text.Json;
using System.Text.Json.Serialization;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// PluginSpec JSON 转换器 - 支持字符串或对象格式
    /// </summary>
    public class PluginSpecConverter : JsonConverter<PluginSpec>
    {
        public override PluginSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var spec = reader.GetString();
                return new PluginSpec { Spec = spec ?? "" };
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;
                
                var pluginSpec = new PluginSpec();
                
                if (root.TryGetProperty("spec", out var specProp))
                {
                    pluginSpec.Spec = specProp.GetString() ?? "";
                }
                
                if (root.TryGetProperty("options", out var optionsProp))
                {
                    pluginSpec.Options = JsonSerializer.Deserialize<Dictionary<string, object>>(optionsProp.GetRawText(), options);
                }
                
                return pluginSpec;
            }

            throw new JsonException($"无法将 {reader.TokenType} 转换为 PluginSpec");
        }

        public override void Write(Utf8JsonWriter writer, PluginSpec value, JsonSerializerOptions options)
        {
            if (value.Options == null || value.Options.Count == 0)
            {
                writer.WriteStringValue(value.Spec);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("spec", value.Spec);
                writer.WritePropertyName("options");
                JsonSerializer.Serialize(writer, value.Options, options);
                writer.WriteEndObject();
            }
        }
    }
}