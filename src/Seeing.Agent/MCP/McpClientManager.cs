using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Seeing.Agent.Core.Interfaces;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Seeing.Agent.MCP
{
    /// <summary>
    /// MCP 客户端管理器 - 管理 MCP Server 连接和工具
    /// 支持 stdio、Streamable HTTP、SSE 三种传输类型
    /// </summary>
    public class McpClientManager
    {
        private readonly ILogger<McpClientManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IHttpClientFactory? _httpClientFactory;
        private readonly ConcurrentDictionary<string, IMcpClientWrapper> _clients = new();
        private readonly ConcurrentDictionary<string, McpTool> _tools = new();

        /// <summary>
        /// 创建 MCP 客户端管理器
        /// </summary>
        public McpClientManager(
            ILogger<McpClientManager> logger,
            ILoggerFactory loggerFactory,
            IHttpClientFactory? httpClientFactory = null)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// 获取所有已注册的 MCP 工具
        /// </summary>
        public IReadOnlyCollection<McpTool> GetTools() => _tools.Values.ToList().AsReadOnly();

        /// <summary>
        /// 获取所有已注册的工具（作为 ITool）
        /// </summary>
        public IEnumerable<ITool> GetToolsAsITools() => _tools.Values.Cast<ITool>();

        /// <summary>
        /// 连接到 MCP Server 并注册工具
        /// </summary>
        public async Task ConnectAsync(McpServerConfig config, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(config.Name))
            {
                _logger.LogWarning("MCP Server 配置缺少名称，已跳过");
                return;
            }

            if (!config.IsValid())
            {
                _logger.LogWarning("MCP Server {Name} 配置无效，已跳过", config.Name);
                return;
            }

            // 检查是否已连接
            if (_clients.ContainsKey(config.Name))
            {
                _logger.LogWarning("MCP Server {Name} 已连接，跳过重复连接", config.Name);
                return;
            }

            _logger.LogInformation("连接 MCP Server: {Name} (Transport: {TransportType})",
                config.Name, config.TransportType);

            try
            {
                // 创建客户端包装器
                var client = CreateClientWrapper(config);
                await client.ConnectAsync(cancellationToken);

                _clients[config.Name] = client;

                // 获取工具列表
                var tools = await client.ListToolsAsync(cancellationToken);
                foreach (var tool in tools)
                {
                    var mcpTool = new McpTool(
                        config.Name,
                        tool.Name,
                        tool.Description ?? "",
                        tool.ParametersSchema,
                        async (toolName, args) => await ExecuteMcpToolAsync(config.Name, toolName, args, cancellationToken)
                    );

                    var toolId = $"{config.Name}_{tool.Name}";
                    _tools[toolId] = mcpTool;

                    _logger.LogDebug("注册 MCP 工具: {ToolId} - {Description}", toolId, tool.Description);
                }

                _logger.LogInformation("MCP Server {Name} 已连接，注册 {Count} 个工具", config.Name, tools.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "连接 MCP Server 失败: {Name}", config.Name);
                throw;
            }
        }

        /// <summary>
        /// 批量连接 MCP Server
        /// </summary>
        public async Task ConnectAsync(IEnumerable<McpServerConfig> configs, CancellationToken cancellationToken = default)
        {
            foreach (var config in configs)
            {
                try
                {
                    await ConnectAsync(config, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "连接 MCP Server {Name} 失败，继续连接其他服务器", config.Name);
                }
            }
        }

        /// <summary>
        /// 执行 MCP 工具
        /// </summary>
        private async Task<McpToolResult> ExecuteMcpToolAsync(
            string serverName,
            string toolName,
            Dictionary<string, object?> args,
            CancellationToken cancellationToken = default)
        {
            if (!_clients.TryGetValue(serverName, out var client))
            {
                return new McpToolResult { IsError = true, Content = $"MCP Server 未连接: {serverName}" };
            }

            try
            {
                return await client.CallToolAsync(toolName, args, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP 工具执行失败: {ServerName}.{ToolName}", serverName, toolName);
                return new McpToolResult { IsError = true, Content = ex.Message };
            }
        }

        /// <summary>
        /// 断开所有连接
        /// </summary>
        public async Task DisconnectAllAsync()
        {
            foreach (var client in _clients.Values)
            {
                try
                {
                    await client.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "断开 MCP 连接失败");
                }
            }

            _clients.Clear();
            _tools.Clear();
        }

        /// <summary>
        /// 获取指定服务器的连接状态
        /// </summary>
        public bool IsConnected(string serverName) => _clients.ContainsKey(serverName);

        /// <summary>
        /// 获取所有已连接的服务器名称
        /// </summary>
        public IReadOnlyCollection<string> GetConnectedServers() => _clients.Keys.ToList().AsReadOnly();

        /// <summary>
        /// 创建 MCP 客户端包装器（根据传输类型）
        /// </summary>
        protected virtual IMcpClientWrapper CreateClientWrapper(McpServerConfig config)
        {
            return config.TransportType switch
            {
                McpTransportType.Stdio => new StdioMcpClientWrapper(config, _loggerFactory.CreateLogger<StdioMcpClientWrapper>()),
                McpTransportType.StreamableHttp => CreateHttpWrapper(config, HttpTransportMode.StreamableHttp),
                McpTransportType.Sse => CreateHttpWrapper(config, HttpTransportMode.Sse),
                _ => throw new NotSupportedException($"不支持的传输类型: {config.TransportType}")
            };
        }

        /// <summary>
        /// 创建 HTTP 传输包装器
        /// </summary>
        private IMcpClientWrapper CreateHttpWrapper(McpServerConfig config, HttpTransportMode mode)
        {
            if (_httpClientFactory == null)
            {
                throw new InvalidOperationException("HTTP 传输需要 IHttpClientFactory，请在 DI 中注册 HttpClient 服务");
            }

            return new HttpMcpClientWrapper(config, _httpClientFactory, _loggerFactory.CreateLogger<HttpMcpClientWrapper>(), mode);
        }
    }

    /// <summary>
    /// MCP 客户端包装器接口
    /// </summary>
    public interface IMcpClientWrapper
    {
        Task ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync();
        Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);
        Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// MCP 工具信息
    /// </summary>
    public class McpToolInfo
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public JsonElement ParametersSchema { get; set; }
    }

    /// <summary>
    /// stdio 传输 MCP 客户端包装器
    /// </summary>
    public class StdioMcpClientWrapper : IMcpClientWrapper
    {
        private readonly McpServerConfig _config;
        private readonly ILogger _logger;
        private McpClient? _mcpClient;
        private StdioClientTransport? _transport;

        public StdioMcpClientWrapper(McpServerConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_config.Command))
            {
                throw new InvalidOperationException("stdio 传输需要配置 command");
            }

            _logger.LogInformation("正在连接 MCP Server (stdio): {Name}, Command: {Command}",
                _config.Name, _config.Command);

            // 创建 Stdio 传输层
            var transportOptions = new StdioClientTransportOptions
            {
                Name = _config.Name,
                Command = _config.Command,
                Arguments = _config.Args?.ToArray() ?? Array.Empty<string>(),
                ShutdownTimeout = _config.ShutdownTimeout
            };

            // 工作目录
            if (!string.IsNullOrEmpty(_config.WorkingDirectory))
            {
                transportOptions.WorkingDirectory = _config.WorkingDirectory;
            }

            // 设置环境变量
            if (_config.Env != null && _config.Env.Count > 0)
            {
                transportOptions.EnvironmentVariables = new Dictionary<string, string?>();
                foreach (var env in _config.Env)
                {
                    transportOptions.EnvironmentVariables[env.Key] = env.Value;
                }
            }

            _transport = new StdioClientTransport(transportOptions);

            // 创建 MCP 客户端
            _mcpClient = await McpClient.CreateAsync(_transport, cancellationToken: cancellationToken);

            _logger.LogInformation("MCP Server {Name} 连接成功", _config.Name);
        }

        public async Task DisconnectAsync()
        {
            if (_mcpClient != null)
            {
                try
                {
                    await _mcpClient.DisposeAsync();
                    _logger.LogInformation("MCP Server {Name} 已断开连接", _config.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "断开 MCP Server {Name} 连接时发生错误", _config.Name);
                }
                finally
                {
                    _mcpClient = null;
                    _transport = null;
                }
            }
        }

        public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            if (_mcpClient == null)
            {
                _logger.LogError("MCP 客户端未连接，无法获取工具列表");
                return new List<McpToolInfo>();
            }

            try
            {
                var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
                var result = new List<McpToolInfo>();

                foreach (var tool in tools)
                {
                    var toolInfo = new McpToolInfo
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        ParametersSchema = tool.JsonSchema
                    };

                    result.Add(toolInfo);
                    _logger.LogDebug("发现 MCP 工具: {Name} - {Description}", tool.Name, tool.Description);
                }

                _logger.LogInformation("从 MCP Server {Name} 获取到 {Count} 个工具", _config.Name, result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 MCP Server {Name} 工具列表失败", _config.Name);
                throw;
            }
        }

        public async Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default)
        {
            if (_mcpClient == null)
            {
                _logger.LogError("MCP 客户端未连接，无法调用工具: {ToolName}", toolName);
                return new McpToolResult { IsError = true, Content = "MCP 客户端未连接" };
            }

            try
            {
                _logger.LogDebug("调用 MCP 工具: {ServerName}.{ToolName}", _config.Name, toolName);

                var result = await _mcpClient.CallToolAsync(
                    toolName,
                    args,
                    cancellationToken: cancellationToken);

                return BuildToolResult(result, _config.Name, toolName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调用 MCP 工具 {ServerName}.{ToolName} 失败", _config.Name, toolName);
                return new McpToolResult
                {
                    IsError = true,
                    Content = $"工具调用异常: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 构建 MCP 工具结果
        /// </summary>
        public static McpToolResult BuildToolResult(CallToolResult result, string serverName, string toolName)
        {
            var contentBuilder = new System.Text.StringBuilder();
            bool hasError = result.IsError ?? false;

            foreach (var content in result.Content)
            {
                if (content is TextContentBlock textBlock)
                {
                    contentBuilder.AppendLine(textBlock.Text);
                }
                else if (content is ImageContentBlock imageBlock)
                {
                    contentBuilder.AppendLine($"[Image: {imageBlock.MimeType}]");
                }
                else if (content is EmbeddedResourceBlock resourceBlock)
                {
                    contentBuilder.AppendLine($"[Resource: {resourceBlock.Resource?.Uri}]");
                }
                else
                {
                    contentBuilder.AppendLine($"[Content: {content.Type}]");
                }
            }

            return new McpToolResult
            {
                IsError = hasError,
                Content = contentBuilder.ToString().TrimEnd()
            };
        }
    }

    /// <summary>
    /// HTTP 传输 MCP 客户端包装器（支持 Streamable HTTP 和 SSE）
    /// </summary>
    public class HttpMcpClientWrapper : IMcpClientWrapper
    {
        private readonly McpServerConfig _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly HttpTransportMode _transportMode;
        private McpClient? _mcpClient;
        private HttpClientTransport? _transport;

        public HttpMcpClientWrapper(
            McpServerConfig config,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            HttpTransportMode transportMode = HttpTransportMode.StreamableHttp)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _transportMode = transportMode;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_config.Url == null)
            {
                throw new InvalidOperationException("HTTP 传输需要配置 endpoint");
            }

            _logger.LogInformation("正在连接 MCP Server (HTTP): {Name}, Endpoint: {Endpoint}",
                _config.Name, _config.Url);

            // 创建 HTTP 传输选项
            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = _config.Url,
                TransportMode = _transportMode,
                ConnectionTimeout = _config.ConnectionTimeout,
                MaxReconnectionAttempts = _config.MaxReconnectionAttempts,
                DefaultReconnectionInterval = _config.ReconnectionInterval
            };

            // 设置请求头
            if (_config.Headers != null && _config.Headers.Count > 0)
            {
                transportOptions.AdditionalHeaders = new Dictionary<string, string>(_config.Headers);
            }

            // 创建 HTTP 客户端和传输层
            var httpClient = _httpClientFactory.CreateClient($"Mcp_{_config.Name}");
            _transport = new HttpClientTransport(transportOptions, httpClient, null, ownsHttpClient: false);

            // 创建 MCP 客户端
            _mcpClient = await McpClient.CreateAsync(_transport, cancellationToken: cancellationToken);

            _logger.LogInformation("MCP Server {Name} 连接成功 (Mode: {Mode})", _config.Name, _transportMode);
        }

        public async Task DisconnectAsync()
        {
            if (_mcpClient != null)
            {
                try
                {
                    await _mcpClient.DisposeAsync();
                    _logger.LogInformation("MCP Server {Name} 已断开连接", _config.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "断开 MCP Server {Name} 连接时发生错误", _config.Name);
                }
                finally
                {
                    _mcpClient = null;
                    _transport = null;
                }
            }
        }

        public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            if (_mcpClient == null)
            {
                _logger.LogError("MCP 客户端未连接，无法获取工具列表");
                return new List<McpToolInfo>();
            }

            try
            {
                var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
                var result = new List<McpToolInfo>();

                foreach (var tool in tools)
                {
                    result.Add(new McpToolInfo
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        ParametersSchema = tool.JsonSchema
                    });
                }

                _logger.LogInformation("从 MCP Server {Name} 获取到 {Count} 个工具", _config.Name, result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 MCP Server {Name} 工具列表失败", _config.Name);
                throw;
            }
        }

        public async Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default)
        {
            if (_mcpClient == null)
            {
                _logger.LogError("MCP 客户端未连接，无法调用工具: {ToolName}", toolName);
                return new McpToolResult { IsError = true, Content = "MCP 客户端未连接" };
            }

            try
            {
                _logger.LogDebug("调用 MCP 工具: {ServerName}.{ToolName}", _config.Name, toolName);

                var result = await _mcpClient.CallToolAsync(toolName, args, cancellationToken: cancellationToken);
                return StdioMcpClientWrapper.BuildToolResult(result, _config.Name, toolName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调用 MCP 工具 {ServerName}.{ToolName} 失败", _config.Name, toolName);
                return new McpToolResult
                {
                    IsError = true,
                    Content = $"工具调用异常: {ex.Message}"
                };
            }
        }
    }
}