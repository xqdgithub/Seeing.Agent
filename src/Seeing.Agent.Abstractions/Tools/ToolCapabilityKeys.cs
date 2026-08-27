namespace Seeing.Agent.Abstractions.Tools
{
    /// <summary>
    /// 工具能力预定义键（kebab-case）。
    /// <para>
    /// 框架消费端（AgentExecutor / CachedToolDecorator / RetryToolDecorator 等）按这些键读取
    /// <see cref="IToolCapabilities.Capabilities"/>。键缺失表示使用框架默认行为。
    /// 时长值统一为「毫秒整数字符串」（与 BashTool timeout、TaskStatusTool timeout_ms 惯例一致）。
    /// </para>
    /// </summary>
    public static class ToolCapabilityKeys
    {
        /// <summary>豁免 AgentExecutor 全局兜底超时（"true"/"false"，默认 false）。工具自身超时仍生效。</summary>
        public const string TimeoutSkip = "timeout.skip";

        /// <summary>工具自身硬超时上限（毫秒）。存在时优先于全局 ToolExecutionTimeout，即使全局未开启也生效。</summary>
        public const string TimeoutBudget = "timeout.budget";

        /// <summary>是否允许 CachedToolDecorator 缓存该工具结果（"true"/"false"，默认 false）。</summary>
        public const string CacheEnabled = "cache.enabled";

        /// <summary>缓存过期时间（毫秒），覆盖装饰器默认。仅在 cache.enabled=true 时生效。</summary>
        public const string CacheTtl = "cache.ttl";

        /// <summary>缓存键作用域（"global"/"session"，默认 session）。session 时键含 SessionId。</summary>
        public const string CacheScope = "cache.scope";

        // === 预留键（本期定义但框架暂不消费，纳入规范供未来扩展）===

        /// <summary>跳过资源级权限检查（预留）。</summary>
        public const string PermissionSkip = "permission.skip";

        /// <summary>工具幂等（预留，影响重试决策）。</summary>
        public const string Idempotent = "idempotent";

        /// <summary>是否允许 RetryToolDecorator 重试（预留）。</summary>
        public const string RetryEnabled = "retry.enabled";
    }
}
