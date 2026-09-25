using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Permissions;
using System.Text.Json;

namespace Seeing.Agent.Mcp;

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

    /// <summary>工具标签（用于分类和过滤）</summary>
    public IReadOnlyList<string> Tags => Array.Empty<string>();

    /// <summary>工具分类</summary>
    public ToolCategory Category => ToolCategory.General;

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
        // server 粒度资源门：以 mcp.execute kind、resource=server 名发起审批（无授权器时跳过）。
        var gate = await McpToolPermissionGate
            .AuthorizeAsync(_serverName, _realName, arguments, context)
            .ConfigureAwait(false);

        if (gate is { Decision: not PermissionEffect.Allow })
        {
            return new ToolResult
            {
                Success = false,
                Title = "MCP 授权未通过",
                Output = gate.Reason ?? "权限被拒绝",
                Error = gate.Reason ?? "Permission denied"
            };
        }

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
            // 对齐统一信封：异常信息进 Error 字段，供上层统一渲染
            return new ToolResult
            {
                Success = false,
                Title = "MCP 执行错误",
                Output = ex.Message,
                Error = ex.Message
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
