namespace Seeing.Agent.Abstractions.Permissions
{
    /// <summary>
    /// 统一的权限请求
    /// </summary>
    public class PermissionRequest
    {
        /// <summary>权限类别："tool.execute", "filesystem.write", "filesystem.read", "network.fetch" 等</summary>
        public string PermissionKind { get; set; } = string.Empty;

        /// <summary>目标资源（工具名、文件路径、URL 等）</summary>
        public string? Resource { get; set; }

        /// <summary>辅助匹配模式列表</summary>
        public List<string> Patterns { get; set; } = new();

        /// <summary>上下文元数据</summary>
        public Dictionary<string, object> Metadata { get; set; } = new();

        /// <summary>当前会话 ID</summary>
        public string? SessionId { get; set; }
    }

    /// <summary>
    /// 权限请求通道接口 — 统一的权限请求入口。
    /// 记忆检查由外层 SerializingPermissionChannel 处理，具体通道实现只需处理 UI 交互。
    /// </summary>
    public interface IPermissionChannel
    {
        /// <summary>统一的权限请求入口</summary>
        Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default);
    }

    /// <summary>
    /// 权限请求异常
    /// </summary>
    public class PermissionRequiredException : Exception
    {
        public string Resource { get; }

        public PermissionRequiredException(string resource, string message)
            : base($"[{resource}] {message}")
        {
            Resource = resource;
        }
    }
}
