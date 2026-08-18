using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Configuration
{
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

}
