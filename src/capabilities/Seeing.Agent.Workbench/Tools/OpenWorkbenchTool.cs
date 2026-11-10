using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Tools.Support;
using Seeing.Session.Core;
using System.Text.Json;

namespace Seeing.Agent.Workbench.Tools;

/// <summary>
/// <c>open_workbench</c>：在会话页打开一个工作台（或把已打开的那个切到前台）。
/// <para>
/// 五道门闸，每道都<b>给出自解释失败文案</b>而不是静默成功：
/// ① 扩展点未声明（无界面宿主）；② 工作台 id 未知（附当前可用 id）；
/// ③ 声明存在但依赖模块未启用（附 <c>Requires</c> 中未启用的项）；
/// ④ 载荷浅校验失败（附缺失/类型错误的键）；⑤ 无可呈现界面（明确失败，不假装打开）。
/// </para>
/// <para>载荷语义由工作台自己解释；本工具只做「required 键存在 + 基本类型」的浅校验。</para>
/// </summary>
public sealed class OpenWorkbenchTool : BuiltInToolBase
{
    private readonly IUiContributionQuery _query;
    private readonly IUiContributionRegistry _registry;
    private readonly IUiPointCatalog? _points;

    /// <summary>创建工具。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="query">可见贡献查询（判定与宿主渲染同源）。</param>
    /// <param name="registry">贡献事实表（用于区分「未知 id」与「模块未启用」）。</param>
    /// <param name="points">扩展点目录（null = 无界面宿主）。</param>
    public OpenWorkbenchTool(
        ILogger<OpenWorkbenchTool> logger,
        IUiContributionQuery query,
        IUiContributionRegistry registry,
        IUiPointCatalog? points = null) : base(logger)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _points = points;
    }

    /// <inheritdoc />
    public override string Id => "open_workbench";

    /// <inheritdoc />
    public override string Description =>
        "在用户界面的侧栏工作台打开一个视图（如文件、Markdown 预览），可带打开参数并指定左右侧。\n\n" +
        "- 工作台 id 先由 `list_workbenches` 获得；未知 id 会返回当前可用 id。\n" +
        "- payload 结构由该工作台定义，见 `list_workbenches` 的「打开参数」。\n" +
        "- dock 可选 left/right；同一工作台在两侧都已打开时必须指定。\n" +
        "- 仅呈现，不读文件内容（读内容用 `read`）。";

    /// <inheritdoc />
    public override ToolCategory Category => ToolCategory.General;

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            workbenchId = new { type = "string", description = "工作台 id（由 list_workbenches 获得）" },
            payload = new { type = "object", description = "打开参数（结构由该工作台定义，可选）" },
            dock = new
            {
                type = "string",
                description = "停靠位：left 或 right（可选，缺省用该工作台的默认停靠位）",
                @enum = new[] { "left", "right" }
            }
        },
        required = new[] { "workbenchId" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        // ① 扩展点
        if (_points?.Find(WorkbenchPoints.DockId) is null)
            return Failure("当前宿主未声明工作台扩展点（无可用界面），无法打开工作台");

        var workbenchId = GetStringArgument(arguments, "workbenchId");
        if (string.IsNullOrWhiteSpace(workbenchId))
            return Failure("缺少必需参数: workbenchId");

        workbenchId = workbenchId.Trim();

        if (string.Equals(workbenchId, WorkbenchRequestKinds.ReservedRegistryId, StringComparison.Ordinal))
            return Failure($"'{WorkbenchRequestKinds.ReservedRegistryId}' 是保留 id，不能作为工作台打开");

        var scenario = ResolveSessionScenario(context);
        var visible = _query.GetVisible<WorkbenchContribution>(WorkbenchPoints.DockId, scenario);

        // ② 未知 id / ③ 模块未启用：用声明全集区分两种失败
        var workbench = visible.FirstOrDefault(w => string.Equals(w.Id, workbenchId, StringComparison.Ordinal));
        if (workbench is null)
        {
            var declared = _registry.Get<WorkbenchContribution>(WorkbenchPoints.DockId)
                .FirstOrDefault(w => string.Equals(w.Id, workbenchId, StringComparison.Ordinal));

            if (declared is not null)
            {
                // 声明在表内但不可见：要么模块未启用，要么被会话工作模式收窄
                var disabled = DisabledModules(declared, context);
                if (disabled.Length > 0)
                    return Failure($"工作台 '{workbenchId}' 依赖的模块未启用：{string.Join(", ", disabled)}");

                return Failure(
                    $"工作台 '{workbenchId}' 被当前会话工作模式隐藏（模式：{scenario ?? "跟随进程"}）；" +
                    "请切换会话工作模式或改用进程级模式");
            }

            var ids = visible.Count == 0
                ? "（当前没有可用工作台）"
                : string.Join(", ", visible.Select(w => $"`{w.Id}`"));
            return Failure($"未知工作台 '{workbenchId}'。当前可用：{ids}");
        }

        // ④ 载荷浅校验
        JsonElement? payload = arguments.TryGetProperty("payload", out var rawPayload)
                               && rawPayload.ValueKind == JsonValueKind.Object
            ? rawPayload
            : null;

        if (ValidatePayload(workbench, payload) is { } payloadError)
            return Failure(payloadError);

        var dockText = GetStringArgument(arguments, "dock");
        var dock = ParseDock(dockText);
        if (dockText is { Length: > 0 } && dock is null)
            return Failure($"dock 只能是 left 或 right，收到 '{dockText}'");

        // ⑤ 可呈现界面 + 通道
        if (context.Services?.GetService(typeof(IHostSurfaceRegistry)) is IHostSurfaceRegistry surfaces
            && !surfaces.CanSurface(context.SessionId))
        {
            return Failure("没有可呈现该会话的界面，请先在浏览器/桌面端打开该会话页面");
        }

        if (context.Services?.GetService(typeof(IHostRequestManager)) is not IHostRequestManager manager)
            return Failure("宿主请求通道未接入，无法驱动界面");

        var request = new HostRequest
        {
            SessionId = context.SessionId,
            CallId = context.CallId,
            Kind = WorkbenchRequestKinds.RegistryOpen,
            Payload = JsonSerializer.SerializeToElement(new
            {
                workbenchId,
                dock = dock?.ToString(),
                openPayload = payload
            })
        };

        try
        {
            var ticket = await manager.BeginAsync(request, context.CancellationToken).ConfigureAwait(false);
            var response = await manager.WaitAsync(ticket, context.CancellationToken).ConfigureAwait(false);

            return response.Status switch
            {
                HostResponseStatus.Completed => Success(
                    $"已打开工作台：{workbench.Title}（{(dock ?? workbench.DefaultDock) switch
                    {
                        WorkbenchDock.Left => "左侧",
                        _ => "右侧"
                    }}）",
                    new Dictionary<string, object>
                    {
                        ["status"] = "completed",
                        ["workbench_id"] = workbenchId,
                        ["dock"] = (dock ?? workbench.DefaultDock).ToString(),
                        ["request_id"] = response.RequestId
                    }),

                HostResponseStatus.Unavailable => Failure("界面未就绪，工作台未能打开"),
                HostResponseStatus.Timeout => Failure("界面未在限定时间内响应"),
                HostResponseStatus.Cancelled => Failure("打开工作台已取消"),
                _ => Failure(response.Reason ?? "打开工作台失败")
            };
        }
        catch (OperationCanceledException)
        {
            return Failure("打开工作台已取消");
        }
    }

    private static string[] DisabledModules(WorkbenchContribution workbench, ToolContext context)
    {
        if (workbench.Requires.Count == 0)
            return [];

        if (context.Services?.GetService(typeof(IModuleCatalog)) is not IModuleCatalog catalog)
            return [];

        return workbench.Requires.Where(id => !catalog.IsEnabled(id)).ToArray();
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

    private static WorkbenchDock? ParseDock(string? text)
        => text?.Trim().ToLowerInvariant() switch
        {
            "left" => WorkbenchDock.Left,
            "right" => WorkbenchDock.Right,
            _ => null
        };

    /// <summary>
    /// 浅校验：只查 <c>required</c> 键是否存在、基本类型是否匹配。
    /// <para>不做完整 JSON Schema 引擎——深度校验（取值范围、业务约束）归工作台自己。</para>
    /// </summary>
    internal static string? ValidatePayload(WorkbenchContribution workbench, JsonElement? payload)
    {
        if (workbench.OpenPayloadSchema is not { ValueKind: JsonValueKind.Object } schema)
            return null;

        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
            return null;

        var requiredKeys = required.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        if (requiredKeys.Length == 0)
            return null;

        if (payload is not { ValueKind: JsonValueKind.Object } actual)
            return $"工作台 '{workbench.Id}' 需要参数：{string.Join(", ", requiredKeys)}";

        var problems = new List<string>();
        foreach (var key in requiredKeys)
        {
            if (!actual.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                problems.Add($"缺少 '{key}'");
                continue;
            }

            if (schema.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(key, out var propertySchema)
                && propertySchema.ValueKind == JsonValueKind.Object
                && propertySchema.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && TypeMismatch(typeElement.GetString()!, value) is { } mismatch)
            {
                problems.Add($"'{key}' {mismatch}");
            }
        }

        return problems.Count == 0
            ? null
            : $"工作台 '{workbench.Id}' 的参数不合法：{string.Join("；", problems)}";
    }

    private static string? TypeMismatch(string expectedType, JsonElement value) => expectedType switch
    {
        "string" when value.ValueKind != JsonValueKind.String => "类型应为 string",
        "integer" when value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _) => "类型应为 integer",
        "number" when value.ValueKind != JsonValueKind.Number => "类型应为 number",
        "boolean" when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) => "类型应为 boolean",
        "object" when value.ValueKind != JsonValueKind.Object => "类型应为 object",
        "array" when value.ValueKind != JsonValueKind.Array => "类型应为 array",
        _ => null
    };
}
