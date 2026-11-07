using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Serialization;
using Seeing.Agent.Core.Tools;
using System.Text.Json;

namespace Seeing.Agent.Core;

/// <summary>
/// 工具可见性判定与定义回灌。
/// <para>
/// <b>职责边界：</b>回答两个问题——「这个工具名能不能调」（结算内 / 被禁用 / 被过滤 / 不存在），
/// 以及「调用失败时该不该附完整定义」。两者都只读本轮冻结快照（<see cref="AgentContext.ToolSettlement"/>），
/// <b>不重新结算</b>，因此可安全地在每次工具调用中重复调用。
/// </para>
/// <para>
/// <b>为什么不与 <see cref="ToolManager"/> 合并：</b>这里判定的是「本会话结算 + 本 Agent 过滤后的可用性」，
/// 属于执行期语义；<see cref="ToolManager"/> 只持有进程级注册表与全局启用状态。混在一处会让
/// 「注册表里有什么」与「本轮能用什么」再次被混淆。
/// </para>
/// </summary>
internal static class ToolVisibilityGate
{
    /// <summary>回灌定义的大小上限（字符）；超过则不附，改提示用检索获取。</summary>
    internal const int MaxDefinitionChars = 8192;

    /// <summary>
    /// 把模型给的工具名归一化为结算里的 canonical id；不在结算内时返回 null。
    /// <para>结算用 OrdinalIgnoreCase、<see cref="ToolManager"/> 用序数敏感，故必须先归一化。</para>
    /// </summary>
    public static string? ResolveCanonicalName(string name, AgentContext context)
        => context.ToolSettlement?.ResolveCanonicalId(name);

    /// <summary>
    /// 结算外工具的三种拒绝文案：已禁用 / 已注册但被过滤 / 不存在。
    /// <para>
    /// 「被过滤」必须明确说检索解决不了：<c>tool_search</c> 的语料取自结算内的 deferred 集，
    /// 被过滤的工具不在语料里，提示检索只会让模型反复空搜。
    /// </para>
    /// </summary>
    /// <param name="name">模型给出的工具名。</param>
    /// <param name="context">执行上下文（读取结算快照以判断是否有检索入口）。</param>
    /// <param name="isEnabled">该工具在全局是否启用（由调用方的唯一查找 helper 提供）。</param>
    /// <param name="isRegistered">
    /// 该名称是否存在于注册表（不论是否被过滤/禁用）。由调用方提供而非在此枚举注册表——
    /// 注册表枚举必须收敛到 <c>AgentExecutor</c> 的单一查找 helper（见 ToolViewSingleSourceTests）。
    /// </param>
    public static string ResolveVisibilityRejection(
        string name,
        AgentContext context,
        bool isEnabled,
        bool isRegistered)
    {
        if (!isEnabled)
            return $"工具 '{name}' 已被禁用；可在工具设置中启用后重试。";

        if (isRegistered)
            return $"工具 '{name}' 在当前 Agent 或会话中不可用；" +
                   "这不是检索可以解决的问题，请直接使用已提供的工具完成任务。";

        var hint = context.ToolSettlement?.HasSearchTool == true
            ? $"\n可用 {ToolIdConventions.SearchToolId} 按关键词检索你要的能力（例如 query=\"删除文件\"）。"
            : string.Empty;
        return $"无此工具 '{name}'。{hint}";
    }

    /// <summary>拒绝原因的结构化元数据（供 UI / 审计按枚举判定，不解析文案）。</summary>
    public static IReadOnlyDictionary<string, object> BuildVisibilityMetadata(
        bool isEnabled,
        bool isRegistered) =>
        new Dictionary<string, object>
        {
            ["visibility.reason"] =
                !isEnabled ? "disabled"
                : isRegistered ? "not_in_settlement"
                : "not_found"
        };

    /// <summary>
    /// 判断失败是否属「参数类」（缺参 / 类型错 / 枚举值非法）。
    /// <para>
    /// 超时、重试耗尽、权限拒绝这些失败附定义没有帮助，只会白付 2–8KB。
    /// 权限拒绝按结构化标记判定（<c>permission.denied</c>），不依赖错误文案——
    /// 真实拒绝的 <c>Error</c> 是策略给出的原因，文案千变万化。
    /// </para>
    /// </summary>
    public static bool IsParamLikeFailure(ToolResult result)
    {
        if (result.Success)
            return false;
        if (result.Metadata?.ContainsKey("timeout") == true)
            return false;
        if (result.Metadata?.ContainsKey("permission.denied") == true)
            return false;
        if (string.Equals(result.Title, "重试耗尽", StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>
    /// 异常是否值得回灌定义。取消与 IO/网络类异常不是参数问题，附定义只会浪费上下文。
    /// </summary>
    public static bool IsParamLikeException(Exception ex) =>
        ex is not OperationCanceledException
        && ex is not TimeoutException
        && ex is not IOException
        && ex is not UnauthorizedAccessException;

    /// <summary>
    /// 从本轮冻结快照取该工具的完整定义（不重新结算）。
    /// 超过 <see cref="MaxDefinitionChars"/> 时返回 null——由调用方改写成检索提示。
    /// </summary>
    public static string? ResolveFailureDefinition(string name, AgentContext context)
    {
        var exposed = context.ToolSettlement?.Tools.FirstOrDefault(
            t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));
        if (exposed?.Schema is null)
            return null;

        var json = JsonSerializer.Serialize(exposed.Schema, SeeingJson.Readable);
        return json.Length <= MaxDefinitionChars ? json : null;
    }

    /// <summary>
    /// 需要回灌但取不到（超限 / 快照缺失）时的回退提示：
    /// 不留空承诺——告诉模型定义为什么没给、去哪里取（无检索入口时不提它）。
    /// </summary>
    public static string AppendDefinitionFallbackHint(string? error, string name, AgentContext context)
    {
        var head = string.IsNullOrEmpty(error) ? "(未提供错误详情)" : error;
        var hint = context.ToolSettlement?.HasSearchTool == true
            ? $"请用 {ToolIdConventions.SearchToolId} query=\"{name}\" detail=full 获取。"
            : "该工具本轮无可用的定义与检索入口，请按目录中的参数签名修正后重试。";
        return head +
               $"\n\n未能附上该工具的完整定义（上限 {MaxDefinitionChars} 字符或快照不可用）。" +
               hint;
    }
}
