using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Tools.Support;
using Seeing.Session.Core;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Workbench.Tools;

/// <summary>
/// <c>list_workbenches</c>：列出当前可打开的工作台。
/// <para>
/// <b>不依赖 UI</b>：只读注册表 + 可见性判定。无界面宿主会得到「无可用工作台」（那是事实，不是错误）。
/// 供 agent「先列再开」，让 <c>open_workbench</c> 的 id 与载荷不必猜。
/// </para>
/// </summary>
public sealed class ListWorkbenchesTool : BuiltInToolBase
{
    private readonly IUiContributionQuery _query;
    private readonly IUiPointCatalog? _points;

    /// <summary>创建工具。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="query">可见贡献查询（判定与宿主渲染同源）。</param>
    /// <param name="points">扩展点目录（null = 无界面宿主）。</param>
    public ListWorkbenchesTool(
        ILogger<ListWorkbenchesTool> logger,
        IUiContributionQuery query,
        IUiPointCatalog? points = null) : base(logger)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _points = points;
    }

    /// <inheritdoc />
    public override string Id => "list_workbenches";

    /// <inheritdoc />
    public override string Description =>
        "列出当前可打开的工作台（会话侧栏的可停靠视图）及其打开参数提示。\n\n" +
        "- 只列当前已启用且可用的工作台；无界面宿主返回空列表。\n" +
        "- 用返回的 id 调 `open_workbench`；payload 按其「打开参数」构造。\n" +
        "- 纯文本读取用 `read`，编辑文件用 `open_file`。";

    /// <inheritdoc />
    public override ToolCategory Category => ToolCategory.General;

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { }
    });

    /// <inheritdoc />
    public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (_points?.Find(WorkbenchPoints.DockId) is null)
        {
            return Task.FromResult(Success(
                "当前宿主未声明工作台扩展点（无可用界面）。\n\n可用工作台：0"));
        }

        var workbenches = _query.GetVisible<WorkbenchContribution>(
            WorkbenchPoints.DockId, ResolveSessionScenario(context));

        var builder = new StringBuilder();
        builder.Append("可用工作台：").Append(workbenches.Count).AppendLine().AppendLine();

        if (workbenches.Count == 0)
        {
            builder.Append("（当前没有已启用且可用的工作台；检查提供它的模块是否被停用，或会话工作模式是否收窄）");
            return Task.FromResult(Success(builder.ToString(), new Dictionary<string, object> { ["count"] = 0 }));
        }

        foreach (var workbench in workbenches)
            RenderOne(builder, workbench);

        return Task.FromResult(Success(builder.ToString().TrimEnd(), new Dictionary<string, object>
        {
            ["count"] = workbenches.Count,
            ["workbenches"] = string.Join(",", workbenches.Select(w => w.Id))
        }));
    }

    private static void RenderOne(StringBuilder builder, WorkbenchContribution workbench)
    {
        builder.Append("- `").Append(workbench.Id).Append("` ").AppendLine(workbench.Title);
        builder.Append("  默认停靠：").AppendLine(workbench.DefaultDock == WorkbenchDock.Left ? "左" : "右");
        builder.Append("  提供上下文：").AppendLine(workbench.ContributesContext ? "是" : "否");

        if (workbench.Requires.Count > 0)
            builder.Append("  依赖模块：").AppendLine(string.Join(", ", workbench.Requires));

        if (workbench.ProvidedTools.Count > 0)
            builder.Append("  相关工具：").AppendLine(string.Join(", ", workbench.ProvidedTools));

        builder.Append("  打开参数：")
            .AppendLine(workbench.OpenPayloadSchema is { } schema ? schema.GetRawText() : "无")
            .AppendLine();
    }

    /// <summary>会话级工作模式（null = 跟随进程级）。经 <see cref="ISessionManager"/> 读会话事实。</summary>
    private static string? ResolveSessionScenario(ToolContext context)
    {
        if (string.IsNullOrEmpty(context.SessionId))
            return null;

        return context.Services?.GetService(typeof(ISessionManager)) is ISessionManager sessions
            ? sessions.Get(context.SessionId)?.Scenario
            : null;
    }
}
