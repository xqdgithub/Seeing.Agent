using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 聊天执行上下文 - 内部使用的完整执行上下文
/// <para>
/// 从公开接口的最小参数构建，包含执行所需的所有信息。
/// </para>
/// </summary>
internal class ChatExecutionContext
{
    // ========== 会话相关 ==========

    /// <summary>会话 ID</summary>
    public required string SessionId { get; init; }

    /// <summary>对话历史</summary>
    public required List<ChatMessage> History { get; set; }

    // ========== Agent 相关 ==========

    /// <summary>Agent 定义</summary>
    public required AgentDefinition Agent { get; init; }

    /// <summary>工作目录</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>工作区根目录</summary>
    public string? WorkspaceRoot { get; init; }

    // ========== 权限相关 ==========

    /// <summary>执行级权限授权器（供能力门 / 资源门）</summary>
    public IPermissionAuthorizer? PermissionAuthorizer { get; init; }

    /// <summary>权限上下文</summary>
    public PermissionContext? PermissionContext { get; init; }

    // ========== 子代理相关 ==========

    /// <summary>父会话 ID（子代理调用时设置）</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>父代理名称</summary>
    public string? ParentAgentName { get; init; }

    /// <summary>是否为顶层调用（Hook 触发控制）</summary>
    public bool IsTopLevel { get; init; } = true;

    /// <summary>是否为后台执行</summary>
    public bool IsBackground { get; init; }

    // ========== Gateway 相关 ==========

    /// <summary>入站对话路由（与 ChatOptions.ChannelRoute 同源）。</summary>
    public SessionChannelRoute? ChannelRoute { get; init; }

    // ========== 请求级参数 ==========

    /// <summary>
    /// 请求级 Model ID（用户在界面选择的模型）
    /// <para>
    /// 适用于 Native Agent 和 ACP Passthrough。
    /// 优先级：RequestModelId &gt; session &gt; Agent.Model &gt; DefaultModel
    /// </para>
    /// </summary>
    public string? RequestModelId { get; init; }

    /// <summary>请求级思考强度 key（可选；空则由 LlmService 用模型 default 或不传）</summary>
    public string? RequestThinkingEffort { get; init; }

    /// <summary>ACP 透传 session mode（如 build / ask）</summary>
    public string? AcpModeId { get; init; }

    /// <summary>
    /// 本轮工具 schema（经 SessionToolSchemaResolver 计算；经 AgentContext.ToolSchemas 传入 executor）。
    /// </summary>
    public IReadOnlyList<FunctionToolSchema>? ToolSchemas { get; init; }

    /// <summary>本轮提示词分节 id（写入 SchemaSnapshotEvent）。</summary>
    public IReadOnlyList<string> SectionIds { get; init; } = Array.Empty<string>();

    /// <summary>本轮 deferred 工具的目录条目（经预算裁剪，供 ToolCatalog 分节渲染与可观测计数）。</summary>
    public IReadOnlyList<ToolCatalogEntry> CatalogEntries { get; init; } = Array.Empty<ToolCatalogEntry>();

    /// <summary>本轮内联暴露的工具数。</summary>
    public int InlineToolCount { get; init; }

    /// <summary>本轮延迟（进目录）的工具数。</summary>
    public int DeferredToolCount { get; init; }

    /// <summary>本轮内联工具定义估算 token（与目录分列）。</summary>
    public int EstimatedDefinitionTokens { get; init; }

    /// <summary>本轮目录估算 token。</summary>
    public int CatalogTokens { get; init; }

    /// <summary>本轮生效的暴露策略模式（反映实际行为）。</summary>
    public ToolExposurePolicyMode ExposureMode { get; init; } = ToolExposurePolicyMode.Auto;

    /// <summary>本轮目录正文（由 ToolCatalogRenderer 渲染；无延迟工具时为空，供 ToolCatalog 分节透传）。</summary>
    public string? CatalogText { get; init; }

    // ========== 元数据 ==========

    /// <summary>附加元数据</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>
    /// 创建子代理执行上下文
    /// </summary>
    public ChatExecutionContext CreateSubAgentContext(
        string subSessionId,
        AgentDefinition subAgent,
        string parentAgentName)
    {
        return new ChatExecutionContext
        {
            SessionId = subSessionId,
            Agent = subAgent,
            History = new List<ChatMessage>(),
            WorkingDirectory = WorkingDirectory,
            WorkspaceRoot = WorkspaceRoot,
            PermissionAuthorizer = PermissionAuthorizer,
            PermissionContext = PermissionContext,  // 权限上下文由 PermissionContext.CreateSubAgentContext 创建
            ParentSessionId = SessionId,
            ParentAgentName = parentAgentName,
            IsTopLevel = false,
            IsBackground = true,
            ToolSchemas = ToolSchemas,
            SectionIds = SectionIds,
            Metadata = new Dictionary<string, object>(Metadata)
        };
    }
}
