using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Decorators;

namespace Seeing.Agent.Helpers
{
    /// <summary>
    /// 工具能力读取辅助。框架消费端统一经此读取 <see cref="IToolCapabilities.Capabilities"/>。
    /// <para>
    /// 消费端拿到的工具可能是装饰器包装后的（Retry/Cache…），统一先解包到最内层原始工具再读字典，
    /// 兼容第三方装饰器未透传能力的情况。
    /// </para>
    /// </summary>
    public static class ToolCapabilityReader
    {
        /// <summary>解包到最内层原始工具（非装饰器时原样返回）。</summary>
        public static ITool Innermost(ITool tool)
            => (tool as ToolDecorator)?.GetInnermostTool() ?? tool;

        /// <summary>读取能力值；缺失或工具未声明返回 null。</summary>
        public static string? Get(ITool? tool, string key)
        {
            if (tool == null)
                return null;

            var innermost = Innermost(tool);
            var caps = (innermost as IToolCapabilities)?.Capabilities;
            if (caps != null && caps.TryGetValue(key, out var value))
                return value;
            return null;
        }

        /// <summary>读取布尔能力值；仅认 "true"（大小写不敏感），缺失返回 defaultValue。</summary>
        public static bool GetBool(ITool? tool, string key, bool defaultValue = false)
        {
            var value = Get(tool, key);
            return string.IsNullOrEmpty(value) ? defaultValue : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>读取毫秒时长能力值；解析失败或缺失返回 null。</summary>
        public static TimeSpan? GetDurationMs(ITool? tool, string key)
        {
            var value = Get(tool, key);
            if (string.IsNullOrEmpty(value) || !long.TryParse(value, out var ms) || ms <= 0)
                return null;
            return TimeSpan.FromMilliseconds(ms);
        }

        /// <summary>读取缓存作用域；缺失返回默认值（"session"）。</summary>
        public static string GetCacheScope(ITool? tool, string defaultValue = "session")
        {
            var value = Get(tool, ToolCapabilityKeys.CacheScope);
            return string.Equals(value, "global", StringComparison.OrdinalIgnoreCase) ? "global" : defaultValue;
        }
    }
}
