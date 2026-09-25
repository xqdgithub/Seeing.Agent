using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Question;

/// <summary>
/// 问答工具模块 — 提供 question（结构化单选/多选/文本提问并等待用户作答）。
/// <para>只依赖 Abstractions + Tools.Support；不引用 Core。</para>
/// </summary>
public sealed class QuestionToolsModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools = ["question"];

    /// <inheritdoc />
    public string Id => "question.tools";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<QuestionTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        await tm.RegisterToolAsync(
            services.GetRequiredService<QuestionTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
