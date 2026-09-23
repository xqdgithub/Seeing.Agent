using Seeing.Agent.Abstractions.Permissions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering.Prompts;

/// <summary>权限提示决策结果（<see cref="PermissionEffect.Allow"/> / <see cref="PermissionEffect.Deny"/> 与作用域）。</summary>
public sealed record PermissionPromptResult(PermissionEffect Effect, PermissionGrantScope Scope);

/// <summary>
/// 权限内联提示：Panel 概要一次性显示后，选项经自绘列表控件 <see cref="TuiListPrompt"/>
/// （键盘 ↑/↓+Enter+Esc、鼠标 hover/点击）呈现，选项按 <see cref="PermissionRequest.AllowedScopes"/> 过滤。
/// <para>
/// 经 <see cref="ITerminalSurface.PromptListAsync{T}"/> 在渲染线程执行以保证单写者；本类<b>不</b>调用 Manager，
/// 裁决由引擎经 <c>TuiPermissionQueue.TryResolve</c> 回传。取消（Esc / 无按键通道）返回 <c>null</c> 表示不决策（保持挂起）。
/// </para>
/// </summary>
public static class PermissionPrompt
{
    private const string CancelSentinel = "\u0000__tui_permission_cancel__";

    /// <summary>呈现权限请求并返回决策；取消/无可用选项时返回 <c>null</c>。</summary>
    public static async Task<PermissionPromptResult?> ShowAsync(
        ITerminalSurface surface,
        PermissionRequest request,
        string? ownerLabel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(request);

        var choices = BuildChoices(request);
        if (choices.Count == 0)
            return null;

        var labels = new List<string>(choices.Count);
        foreach (var choice in choices)
            labels.Add(choice.Label);

        // Choice.Cancel 不作为列表项渲染（BuildChoices 从不加入），IndexOf 恒为 -1：
        // 仅作 Esc/降级时由 TuiListPrompt 原样回传的「取消哨兵下标」，本方法据此收敛为 null（不决策）。
        var cancelIndex = choices.IndexOf(Choice.Cancel);

        var selected = await surface.PromptListAsync(async (ctx, promptCt) =>
        {
            ctx.Console.Write(BuildSummary(request, ownerLabel));
            // Panel 与列表首行之间留一空行：防自绘列表首行贴住 Panel 底边（W1-B 遗留风险 1）。
            ctx.Console.WriteLine();
            return await TuiListPrompt
                .SelectAsync(ctx, "请选择", labels, pageSize: choices.Count, cancelIndex, promptCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (selected is null || selected < 0 || selected == cancelIndex)
            return null;

        var decision = choices[selected.Value];
        return decision.Effect is null || decision.Scope is null
            ? null
            : new PermissionPromptResult(decision.Effect.Value, decision.Scope.Value);
    }

    private static List<Choice> BuildChoices(PermissionRequest request)
    {
        var scopes = request.AllowedScopes is { Count: > 0 }
            ? request.AllowedScopes
            : new[] { PermissionGrantScope.Once, PermissionGrantScope.Session };

        var choices = new List<Choice>();
        if (scopes.Contains(PermissionGrantScope.Once))
            choices.Add(new Choice("本次允许", PermissionEffect.Allow, PermissionGrantScope.Once));
        if (scopes.Contains(PermissionGrantScope.Session))
            choices.Add(new Choice("始终允许（本会话）", PermissionEffect.Allow, PermissionGrantScope.Session));
        if (scopes.Contains(PermissionGrantScope.SessionDirectory))
            choices.Add(new Choice("允许此目录（会话目录）", PermissionEffect.Allow, PermissionGrantScope.SessionDirectory));

        choices.Add(new Choice("本次拒绝", PermissionEffect.Deny, PermissionGrantScope.Once));
        if (scopes.Contains(PermissionGrantScope.Session))
            choices.Add(new Choice("始终拒绝", PermissionEffect.Deny, PermissionGrantScope.Session));

        return choices;
    }

    private static IRenderable BuildSummary(PermissionRequest request, string? ownerLabel)
    {
        var rows = new List<IRenderable>();
        AddRow(rows, "工具", ResolveToolName(request));
        AddRow(rows, "归属", ownerLabel ?? request.AgentName ?? request.SessionId);
        AddRow(rows, "资源", request.Resource);
        if (request.Patterns is { Count: > 0 })
            AddRow(rows, "匹配", string.Join(", ", request.Patterns));
        AddRow(rows, "风险", request.RiskLevel);
        AddRow(rows, "说明", request.Message);

        return new Panel(new Rows(rows))
            .Header("权限请求")
            .Border(BoxBorder.Rounded);
    }

    private static void AddRow(List<IRenderable> rows, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        rows.Add(new Markup($"[grey]{label.EscapeMarkup()}[/]  {value.EscapeMarkup()}"));
    }

    private static string ResolveToolName(PermissionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Resource))
            return request.Resource;

        foreach (var key in new[] { "tool", "toolName", "name" })
        {
            if (request.Metadata.TryGetValue(key, out var value) && value is not null)
            {
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text!;
            }
        }

        return request.PermissionKind;
    }

    private sealed record Choice(string Label, PermissionEffect? Effect, PermissionGrantScope? Scope)
    {
        public static readonly Choice Cancel = new(CancelSentinel, null, null);
    }
}
