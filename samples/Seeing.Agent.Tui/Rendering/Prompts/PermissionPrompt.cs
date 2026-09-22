using Seeing.Agent.Abstractions.Permissions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering.Prompts;

/// <summary>权限提示决策结果（<see cref="PermissionEffect.Allow"/> / <see cref="PermissionEffect.Deny"/> 与作用域）。</summary>
public sealed record PermissionPromptResult(PermissionEffect Effect, PermissionGrantScope Scope);

/// <summary>
/// 权限内联提示：用 <see cref="SelectionPrompt{T}"/> 呈现请求概要，选项按 <see cref="PermissionRequest.AllowedScopes"/> 过滤。
/// <para>
/// 经 <see cref="ITerminalSurface.PromptAsync{T}"/> 在渲染线程执行以保证单写者；本类<b>不</b>调用 Manager，
/// 裁决由引擎经 <c>TuiPermissionQueue.TryResolve</c> 回传。按 Esc 返回 <c>null</c> 表示不决策（保持挂起）。
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

        var selected = await surface.PromptAsync(async (console, promptCt) =>
        {
            console.Write(BuildSummary(request, ownerLabel));

            var prompt = new SelectionPrompt<Choice>()
                .Title("请选择")
                .PageSize(choices.Count)
                .UseConverter(choice => choice.Label)
                .AddChoices(choices)
                .AddCancelResult(Choice.Cancel);

            return await prompt.ShowAsync(console, promptCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return selected.Effect is null || selected.Scope is null
            ? null
            : new PermissionPromptResult(selected.Effect.Value, selected.Scope.Value);
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
