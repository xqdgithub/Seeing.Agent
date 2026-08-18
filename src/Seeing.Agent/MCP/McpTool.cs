using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Helpers;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.MCP.Policy;
using System.Text.Json;
using System.Text.Json.Serialization;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.MCP
{
    
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
