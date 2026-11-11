using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Files.Hosting;
using Seeing.Agent.Files.Watching;
using System.Text.Json;

namespace Seeing.Agent.Files;

/// <summary>
/// 文件工作区能力模块（无工具；提供 <see cref="IFileWorkspaceFactory"/> 与「文件」工作台声明）。
/// </summary>
public sealed class FileWorkspaceModule : ISeeingModule, IUiContributor
{
    /// <inheritdoc />
    public string Id => "files.workspace";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = ["io.local"];

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<FileWatcherPool>();
        services.TryAddSingleton<IFileWorkspaceFactory, FileWorkspaceFactory>();
        services.AddModuleHostedService<FileWatcherHostedService>();
    }

    /// <summary>
    /// 声明「文件」工作台。
    /// <para>
    /// <b>视图键而不是组件类型</b>：能力包不能引用 <c>samples/Seeing.Agent.WebUI.Files</c>
    /// 的 Razor 组件；宿主在 <c>WebUiWorkbenchViews</c> 里把该键映射到组件。
    /// </para>
    /// <para>
    /// Markdown 预览已合并进本工作台（<c>preview</c> 载荷键 + 标签栏切换按钮）。
    /// 历史 id <c>markdown</c> 由宿主 <c>LegacyWorkbenchAlias</c> 别名为 <c>files</c> + <c>preview=true</c>。
    /// </para>
    /// </summary>
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new WorkbenchContribution
        {
            Point = WorkbenchPoints.DockId,
            Id = "files",
            Title = "文件",
            ViewKey = WorkbenchPoints.ViewKeyFor("files"),
            Icon = "folder",
            Requires = [Id],
            Order = 10,
            DefaultDock = WorkbenchDock.Right,
            // 50%：与迁移前的文件面板一致（单开时占半个屏）
            Sizing = new WorkbenchSizing(DefaultPercent: 50, MinPx: 380),
            OpenPayloadSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "要打开的文件：绝对路径，或相对当前工作目录的路径" },
                    line = new { type = "integer", description = "跳转行号（从 1 开始，可选）" },
                    column = new { type = "integer", description = "跳转列号（从 1 开始，可选）" },
                    preview = new { type = "boolean", description = "是否以预览态打开（仅 Markdown 文件有效，默认 true）" }
                },
                required = new[] { "path" }
            }),
            ProvidedTools = ["open_file", "editor_command", "editor_query", "editor_list_commands"],
            ContributesContext = true
        }
    ];

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
