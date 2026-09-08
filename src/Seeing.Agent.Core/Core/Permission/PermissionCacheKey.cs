namespace Seeing.Agent.Core.Permission
{
    /// <summary>
    /// 权限缓存键 - 用于标识缓存条目
    /// </summary>
    public readonly struct PermissionCacheKey : IEquatable<PermissionCacheKey>
    {
        /// <summary>权限名称</summary>
        public string Permission { get; }

        /// <summary>模式</summary>
        public string Pattern { get; }

        /// <summary>Agent 名称（可选）</summary>
        public string? AgentName { get; }

        /// <summary>
        /// 创建权限缓存键
        /// </summary>
        public PermissionCacheKey(string permission, string pattern, string? agentName = null)
        {
            Permission = permission ?? throw new ArgumentNullException(nameof(permission));
            Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            AgentName = agentName;
        }

        /// <inheritdoc />
        public bool Equals(PermissionCacheKey other)
        {
            return Permission == other.Permission &&
                   Pattern == other.Pattern &&
                   AgentName == other.AgentName;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is PermissionCacheKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(Permission, Pattern, AgentName);
        }

        /// <summary>
        /// 生成缓存键字符串
        /// </summary>
        public override string ToString()
        {
            return AgentName != null
                ? $"{AgentName}:{Permission}:{Pattern}"
                : $"{Permission}:{Pattern}";
        }
    }
}