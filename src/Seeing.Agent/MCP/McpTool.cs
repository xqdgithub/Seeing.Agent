using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Helpers;
using Seeing.Agent.MCP.Core;
using Seeing.Agent.MCP.Policy;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.MCP
{
    /// <summary>
    /// MCP 传输类型
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum McpTransportType
    {
        /// <summary>标准输入输出传输（本地进程）</summary>
        [JsonPropertyName("stdio")]
        Stdio,

        /// <summary>Streamable HTTP 传输（远程服务器，推荐）</summary>
        [JsonPropertyName("streamableHttp")]
        StreamableHttp,

        /// <summary>SSE 传输（旧协议兼容）</summary>
        [JsonPropertyName("sse")]
        Sse
    }

    /// <summary>
    /// MCP 服务器配置（Cursor 风格，小驼峰 JSON 格式）
    /// </summary>
    public class McpServerConfig
    {
        /// <summary>服务器名称（作为字典键，不序列化）</summary>
        [JsonIgnore]
        public string Name { get; set; } = "";

        /// <summary>传输类型：stdio / streamableHttp / sse</summary>
        [JsonPropertyName("transportType")]
        public McpTransportType TransportType { get; set; } = McpTransportType.Stdio;

        // —— stdio 配置 ——

        /// <summary>可执行命令</summary>
        [JsonPropertyName("command")]
        public string? Command { get; set; }

        /// <summary>命令行参数</summary>
        [JsonPropertyName("args")]
        public List<string>? Args { get; set; }

        /// <summary>环境变量</summary>
        [JsonPropertyName("env")]
        public Dictionary<string, string>? Env { get; set; }

        /// <summary>工作目录</summary>
        [JsonPropertyName("workingDirectory")]
        public string? WorkingDirectory { get; set; }

        // —— HTTP 配置 ——

        /// <summary>HTTP 端点 URL</summary>
        [JsonPropertyName("url")]
        public Uri? Url { get; set; }

        /// <summary>HTTP 请求头（认证、自定义元数据等）</summary>
        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        // —— 连接管理 ——

        /// <summary>连接超时（秒）</summary>
        [JsonPropertyName("connectionTimeout")]
        public int ConnectionTimeoutSeconds { get; set; } = 30;

        /// <summary>关闭超时（秒）</summary>
        [JsonPropertyName("shutdownTimeout")]
        public int ShutdownTimeoutSeconds { get; set; } = 10;

        /// <summary>最大重连次数</summary>
        [JsonPropertyName("maxReconnectionAttempts")]
        public int MaxReconnectionAttempts { get; set; } = 5;

        /// <summary>重连间隔（毫秒）</summary>
        [JsonPropertyName("reconnectionInterval")]
        public int ReconnectionIntervalMs { get; set; } = 1000;

        // —— 扩展配置 ——

        /// <summary>Server 优先级（影响重连顺序）</summary>
        [JsonPropertyName("priority")]
        public McpServerPriority Priority { get; set; } = McpServerPriority.Normal;

        /// <summary>Server 级重连策略（覆盖全局策略）</summary>
        [JsonPropertyName("reconnectionPolicy")]
        public McpReconnectionPolicy? ReconnectionPolicy { get; set; }

        /// <summary>是否自动启动（默认 true）</summary>
        [JsonPropertyName("autoStart")]
        public bool AutoStart { get; set; } = true;

        /// <summary>连接超时（秒），覆盖全局配置</summary>
        [JsonPropertyName("connectionTimeoutSeconds")]
        public int? ConnectionTimeoutSecondsOverride { get; set; }

        /// <summary>标签（用于分组和筛选）</summary>
        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        /// <summary>描述（用于 UI 展示）</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>是否禁用（配置存在但不启动）</summary>
        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; } = false;

        /// <summary>配置来源级别（运行时设置，不序列化）</summary>
        [JsonIgnore]
        public McpConfigLevel? ConfigLevel { get; set; }

        // —— OAuth 配置 ——

        /// <summary>OAuth 配置（用于需要授权的远程 MCP 服务器）</summary>
        [JsonPropertyName("oauth")]
        public OAuth.McpOAuthConfig? OAuth { get; set; }

        // —— 便捷属性 ——

        /// <summary>连接超时时间</summary>
        [JsonIgnore]
        public TimeSpan ConnectionTimeout => ConnectionTimeoutSecondsOverride.HasValue
            ? TimeSpan.FromSeconds(ConnectionTimeoutSecondsOverride.Value)
            : TimeSpan.FromSeconds(ConnectionTimeoutSeconds);

        /// <summary>关闭超时时间</summary>
        [JsonIgnore]
        public TimeSpan ShutdownTimeout => TimeSpan.FromSeconds(ShutdownTimeoutSeconds);

        /// <summary>重连间隔时间</summary>
        [JsonIgnore]
        public TimeSpan ReconnectionInterval => TimeSpan.FromMilliseconds(ReconnectionIntervalMs);

        /// <summary>
        /// 验证配置是否有效
        /// </summary>
        public bool IsValid()
        {
            return TransportType == McpTransportType.Stdio
                ? !string.IsNullOrEmpty(Command)
                : Url != null;
        }
    }

    /// <summary>
    /// MCP 工具包装器 - 将 MCP Server 的工具代理为 ITool
    /// </summary>
    public class McpTool : ITool
    {
        private readonly string _serverName;
        private readonly string _realName;
        private readonly string _description;
        private readonly JsonElement _parametersSchema;
        private readonly Func<string, Dictionary<string, object?>, Task<McpToolResult>> _executeFunc;

        public string Id => $"{_serverName}_{_realName}";
        public string ServerName => _serverName;
        public string ToolName => _realName;
        public string Description => _description;
        public JsonElement ParametersSchema => _parametersSchema;

        public McpTool(
            string serverName,
            string realName,
            string description,
            JsonElement parametersSchema,
            Func<string, Dictionary<string, object?>, Task<McpToolResult>> executeFunc)
        {
            _serverName = serverName;
            _realName = realName;
            _description = description;
            _parametersSchema = parametersSchema;
            _executeFunc = executeFunc;
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            try
            {
                var args = arguments.ToDictionary();

                var result = await _executeFunc(_realName, args);

                return new ToolResult
                {
                    Success = !result.IsError,
                    Title = _realName,
                    Output = result.Content,
                    Metadata = new Dictionary<string, object> { ["server"] = _serverName }
                };
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    Success = false,
                    Title = "MCP 执行错误",
                    Output = ex.Message
                };
            }
        }
    }

    /// <summary>
    /// MCP 工具执行结果
    /// </summary>
    public class McpToolResult
    {
        /// <summary>是否错误</summary>
        public bool IsError { get; set; }
        
        /// <summary>返回内容</summary>
        public string Content { get; set; } = "";
    }
}
