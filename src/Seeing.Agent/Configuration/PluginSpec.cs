using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// 插件规格 - 支持字符串或带选项的元组格式
    /// <para>
    /// 配置示例：
    /// <code>
    /// // 字符串格式
    /// "@seeing/analytics@1.0.0"
    /// "./plugins/MyExtension.dll"
    /// 
    /// // 带选项格式
    /// { "spec": "./plugins/MyExtension.dll", "options": { "logLevel": "Debug" } }
    /// </code>
    /// </para>
    /// </summary>
    [JsonConverter(typeof(PluginSpecConverter))]
    public class PluginSpec
    {
        /// <summary>
        /// 插件标识
        /// <para>
        /// 支持格式：
        /// - NuGet 包名：@seeing/analytics@1.0.0
        /// - 文件路径：./plugins/MyExtension.dll
        /// - file:// URL：file://./plugins/MyExtension.dll
        /// </para>
        /// </summary>
        public string Spec { get; set; } = "";

        /// <summary>
        /// 插件选项（可选）
        /// <para>
        /// 传递给 IExtension.InitializeAsync 的自定义配置
        /// </para>
        /// </summary>
        public Dictionary<string, object>? Options { get; set; }

        /// <summary>
        /// 从字符串创建 PluginSpec
        /// </summary>
        public static implicit operator PluginSpec(string spec) => new() { Spec = spec };

        /// <summary>
        /// 转换为字符串
        /// </summary>
        public override string ToString() => Spec;
    }

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