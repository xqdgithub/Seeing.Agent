namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Gateway 服务配置（JSON: SeeingAgent:Gateway）
    /// </summary>
    public class GatewayOptions
    {
        /// <summary>是否启用 Gateway 服务</summary>
        public bool Enabled { get; set; }

        /// <summary>宿主启动后是否自动启动 Gateway（需 Enabled=true）</summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>监听端口</summary>
        public int Port { get; set; } = 8765;

        /// <summary>绑定地址</summary>
        public string BindAddress { get; set; } = "127.0.0.1";

        /// <summary>默认 Agent ID</summary>
        public string? DefaultAgentId { get; set; }

        /// <summary>权限请求超时（秒）</summary>
        public int PermissionTimeoutSeconds { get; set; } = 120;

        /// <summary>权限模式：auto_approve | interactive</summary>
        public string PermissionMode { get; set; } = "interactive";

        /// <summary>是否启用 WebSocket 端点</summary>
        public bool EnableWebSocket { get; set; } = true;

        /// <summary>WebSocket 路径</summary>
        public string WebSocketPath { get; set; } = "/api/gateway/ws";

        /// <summary>WebSocket 心跳间隔（秒）</summary>
        public int WebSocketKeepAliveSeconds { get; set; } = 30;

        /// <summary>是否过滤 reasoning/thinking 增量事件</summary>
        public bool FilterThinking { get; set; } = true;
    }
}
