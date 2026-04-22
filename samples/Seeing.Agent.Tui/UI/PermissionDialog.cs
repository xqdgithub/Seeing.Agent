using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.UI;

/// <summary>
/// 模态权限对话框 - 使用 Spectre.Console 阻塞式确认
/// </summary>
/// <remarks>
/// 设计目标：
/// 1. 阻塞式：等待用户响应后继续执行流程
/// 2. 详情展示：显示工具名、操作、风险提示
/// 3. 超时支持：可配置超时时间，自动拒绝
/// 4. 安全决策：用户明确选择 [允许] 或 [拒绝]
/// </remarks>
public static class PermissionDialog
{
    /// <summary>默认超时时间（秒）</summary>
    private const int DefaultTimeoutSeconds = 60;

    /// <summary>风险提示模板</summary>
    private static readonly Dictionary<PermissionRequestType, string> RiskTemplates = new()
    {
        [PermissionRequestType.Tool] = "工具调用可能修改系统状态或访问敏感数据",
        [PermissionRequestType.SubAgent] = "子代理调用将消耗额外资源和执行时间",
        [PermissionRequestType.FileWrite] = "文件写入将永久修改目标文件，无法撤销",
        [PermissionRequestType.Confirmation] = "此操作需要您的明确授权"
    };

    /// <summary>
    /// 显示模态权限对话框并等待用户响应
    /// </summary>
    /// <param name="args">权限请求事件参数</param>
    /// <param name="timeoutSeconds">超时时间（秒），默认 60 秒</param>
    /// <returns>用户决策结果（Allow/Deny）</returns>
    public static PermissionDecision ShowAndWait(PermissionRequestedEventArgs args, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        // 创建超时取消令牌
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            // 渲染权限详情面板
            RenderPermissionDetails(args);

            // 显示确认提示（阻塞式）
            var allowed = AnsiConsole.Prompt(
                new ConfirmationPrompt("是否允许此操作？")
                {
                    DefaultValue = false,  // 默认拒绝，安全优先
                    ShowChoices = true,
                    ShowDefaultValue = true
                });

            // 返回用户决策
            return allowed
                ? PermissionDecision.Allow("用户允许")
                : PermissionDecision.Deny("用户拒绝");
        }
        catch (OperationCanceledException)
        {
            // 超时处理
            AnsiConsole.MarkupLine($"[yellow]⚠ 权限请求超时 ({timeoutSeconds}秒)，自动拒绝[/]");
            return PermissionDecision.Deny("等待超时，自动拒绝");
        }
        catch (Exception ex)
        {
            // 异常处理
            AnsiConsole.MarkupLine($"[red]❌ 权限对话框异常: {ex.Message}[/]");
            return PermissionDecision.Deny($"对话框异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 显示异步模态权限对话框
    /// </summary>
    /// <param name="args">权限请求事件参数</param>
    /// <param name="cancellationToken">外部取消令牌</param>
    /// <param name="timeoutSeconds">超时时间（秒）</param>
    /// <returns>用户决策结果</returns>
    public static async Task<PermissionDecision> ShowAndWaitAsync(
        PermissionRequestedEventArgs args,
        CancellationToken cancellationToken = default,
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        // 创建链接的超时令牌
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            // 渲染权限详情面板
            RenderPermissionDetails(args);

            // 异步显示确认提示（阻塞式）
            var allowed = await AnsiConsole.PromptAsync(
                new ConfirmationPrompt("是否允许此操作？")
                {
                    DefaultValue = false,
                    ShowChoices = true,
                    ShowDefaultValue = true
                },
                linkedCts.Token);

            return allowed
                ? PermissionDecision.Allow("用户允许")
                : PermissionDecision.Deny("用户拒绝");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ 权限请求超时 ({timeoutSeconds}秒)，自动拒绝[/]");
            return PermissionDecision.Deny("等待超时，自动拒绝");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]❌ 权限对话框异常: {ex.Message}[/]");
            return PermissionDecision.Deny($"对话框异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 渲染权限详情面板
    /// </summary>
    /// <param name="args">权限请求参数</param>
    private static void RenderPermissionDetails(PermissionRequestedEventArgs args)
    {
        var rows = new List<IRenderable>();

        // 1. 请求类型标题
        var headerMarkup = BuildHeaderMarkup(args);
        rows.Add(new Markup(headerMarkup));

        // 2. 请求详情内容
        var detailsRenderable = BuildDetailsRenderable(args);
        rows.Add(detailsRenderable);

        // 3. 风险提示
        var riskMarkup = BuildRiskMarkup(args.RequestType);
        rows.Add(new Markup(riskMarkup));

        // 4. 超时提示
        rows.Add(new Markup($"[dim]超时时间: {args.TimeoutSeconds} 秒[/]"));

        // 渲染整体面板
        var panel = new Panel(new Rows(rows))
        {
            Header = new PanelHeader("[bold yellow]⚠ 权限请求[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow),
            Expand = true,
            Padding = new Padding(1, 1)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// 构建标题行 Markup
    /// </summary>
    private static string BuildHeaderMarkup(PermissionRequestedEventArgs args)
    {
        var icon = GetRequestTypeIcon(args.RequestType);
        var title = args.GetDisplayTitle();
        var escapedTitle = EscapeMarkup(title);

        return $"{icon} [bold yellow]{escapedTitle}[/]";
    }

    /// <summary>
    /// 构建详情内容可渲染对象
    /// </summary>
    private static IRenderable BuildDetailsRenderable(PermissionRequestedEventArgs args)
    {
        var detailRows = new List<IRenderable>();

        switch (args.RequestType)
        {
            case PermissionRequestType.Tool:
                // 工具调用详情
                detailRows.Add(new Markup($"[cyan]工具名称:[/] {EscapeMarkup(args.ToolName ?? "未知")}"));
                if (args.ToolArguments != null)
                {
                    detailRows.Add(new Markup("[dim]───────── 参数 ─────────[/]"));
                    detailRows.Add(RenderArgumentsPreview(args.ToolArguments));
                }
                break;

            case PermissionRequestType.SubAgent:
                // 子代理调用详情
                detailRows.Add(new Markup($"[cyan]代理名称:[/] {EscapeMarkup(args.SubAgentName ?? "未知")}"));
                if (!string.IsNullOrEmpty(args.PromptPreview))
                {
                    detailRows.Add(new Markup("[dim]───────── 提示词预览 ─────────[/]"));
                    detailRows.Add(new Markup($"[grey italic]{EscapeMarkup(args.PromptPreview)}[/]"));
                }
                break;

            case PermissionRequestType.FileWrite:
                // 文件写入详情
                detailRows.Add(new Markup($"[cyan]目标文件:[/] {EscapeMarkup(args.FilePath ?? "未知")}"));
                if (!string.IsNullOrEmpty(args.ContentPreview))
                {
                    detailRows.Add(new Markup("[dim]───────── 内容预览 ─────────[/]"));
                    var preview = args.ContentPreview.Length > 200
                        ? args.ContentPreview.Substring(0, 200) + "..."
                        : args.ContentPreview;
                    detailRows.Add(new Markup($"[grey]{EscapeMarkup(preview)}[/]"));
                }
                break;

            case PermissionRequestType.Confirmation:
                // 基础确认详情
                if (args.Request != null)
                {
                    detailRows.Add(new Markup($"[cyan]权限类型:[/] {EscapeMarkup(args.Request.Permission)}"));
                    if (args.Request.Patterns?.Count > 0)
                    {
                        var patterns = string.Join(", ", args.Request.Patterns);
                        detailRows.Add(new Markup($"[cyan]匹配模式:[/] {EscapeMarkup(patterns)}"));
                    }
                }
                break;
        }

        // 执行上下文信息
        if (args.Context != null)
        {
            detailRows.Add(new Markup("[dim]───────── 执行上下文 ─────────[/]"));
            detailRows.Add(new Markup($"[dim]Session: {EscapeMarkup(args.Context.SessionId ?? "无")}[/]"));
        }

        return new Rows(detailRows);
    }

    /// <summary>
    /// 渲染参数预览
    /// </summary>
    private static IRenderable RenderArgumentsPreview(object? arguments)
    {
        if (arguments == null)
            return new Markup("[dim]无参数[/]");

        try
        {
            // 尝试格式化为 JSON
            var json = System.Text.Json.JsonSerializer.Serialize(
                arguments,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

            var escaped = EscapeMarkup(json);
            return new Markup($"[blue]{escaped}[/]");
        }
        catch
        {
            // 序列化失败，直接显示
            return new Markup($"[blue]{EscapeMarkup(arguments.ToString() ?? "无")}[/]");
        }
    }

    /// <summary>
    /// 构建风险提示 Markup
    /// </summary>
    private static string BuildRiskMarkup(PermissionRequestType type)
    {
        var riskText = RiskTemplates.TryGetValue(type, out var risk)
            ? risk
            : "此操作需要您的明确授权";

        return $"[red bold]⚠ 风险提示: {EscapeMarkup(riskText)}[/]";
    }

    /// <summary>
    /// 获取请求类型图标
    /// </summary>
    private static string GetRequestTypeIcon(PermissionRequestType type)
    {
        return type switch
        {
            PermissionRequestType.Tool => ColorScheme.Icons.Tool,
            PermissionRequestType.SubAgent => "[green]🤖[/]",
            PermissionRequestType.FileWrite => "[yellow]📝[/]",
            PermissionRequestType.Confirmation => "[yellow]🔐[/]",
            _ => "[grey]?[/]"
        };
    }

    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    private static string EscapeMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("[", "[[")
            .Replace("]", "]]");
    }

    /// <summary>
    /// 批量处理权限请求（用于多请求场景）
    /// </summary>
    /// <param name="requests">权限请求列表</param>
    /// <param name="timeoutSeconds">每个请求的超时时间</param>
    /// <returns>决策结果列表</returns>
    public static List<PermissionDecision> ShowAndWaitMultiple(
        IEnumerable<PermissionRequestedEventArgs> requests,
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        var results = new List<PermissionDecision>();
        var index = 1;
        var total = requests.Count();

        foreach (var request in requests)
        {
            AnsiConsole.MarkupLine($"[dim]处理权限请求 {index}/{total}[/]");
            var decision = ShowAndWait(request, timeoutSeconds);
            results.Add(decision);
            index++;

            // 添加分隔线
            AnsiConsole.WriteLine();
        }

        return results;
    }
}