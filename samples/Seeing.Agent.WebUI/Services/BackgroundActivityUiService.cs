using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 后台活动 UI 端口：把可选的能力包 <see cref="IBackgroundActivityManager"/> 与页面解耦。
/// <para>
/// 能力包可能未登记（例如纯渲染/测试宿主未启用后台活动模块），因此一律按可选用法解析：
/// 未注册时 <see cref="List"/> 返回空、<see cref="CancelAsync"/> 返回 false，页面照常渲染。
/// </para>
/// </summary>
public sealed class BackgroundActivityUiService
{
    private readonly IServiceProvider _services;

    public BackgroundActivityUiService(IServiceProvider services)
        => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>后台活动能力是否可用（决定取消按钮是否可交互）。</summary>
    public bool IsAvailable => Resolve() is not null;

    /// <summary>
    /// 列出归属会话的活动（首屏重建的唯一数据来源）。
    /// <para>只读投影：任何异常都按「无活动」处理，不阻断会话加载。</para>
    /// </summary>
    public IReadOnlyList<BackgroundActivitySnapshot> List(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<BackgroundActivitySnapshot>();

        var manager = Resolve();
        if (manager is null)
            return Array.Empty<BackgroundActivitySnapshot>();

        try
        {
            return manager.List(new BackgroundActivityQuery(), sessionId);
        }
        catch
        {
            return Array.Empty<BackgroundActivitySnapshot>();
        }
    }

    /// <summary>
    /// 取消活动（用户显式操作）：越权/不存在返回 false。
    /// </summary>
    public async Task<bool> CancelAsync(
        string activityId,
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(activityId) || string.IsNullOrEmpty(sessionId))
            return false;

        var manager = Resolve();
        if (manager is null)
            return false;

        try
        {
            return await manager
                .CancelAsync(activityId, BackgroundActivityCancelReason.User, sessionId, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取活动输出（分页拼接，最多约 256KB）。
    /// <para>供后台卡片"展开全部"使用；失败按空串处理，不阻断渲染。</para>
    /// </summary>
    public async Task<string> ReadOutputAsync(
        string activityId,
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(activityId) || string.IsNullOrEmpty(sessionId))
            return string.Empty;

        var manager = Resolve();
        if (manager is null)
            return string.Empty;

        const int pageSize = 32 * 1024;
        const int hardLimit = 256 * 1024;

        var builder = new System.Text.StringBuilder();
        long offset = 0;

        try
        {
            while (builder.Length < hardLimit)
            {
                var page = await manager
                    .ReadOutputAsync(activityId, offset, pageSize, sessionId, ct)
                    .ConfigureAwait(false);

                if (page.Content.Length == 0)
                    break;

                builder.Append(page.Content);

                if (page.EndOfStream || page.NextOffset <= offset)
                    break;

                offset = page.NextOffset;
            }
        }
        catch
        {
            return builder.ToString();
        }

        return builder.ToString();
    }

    private IBackgroundActivityManager? Resolve()
        => _services.GetService(typeof(IBackgroundActivityManager)) as IBackgroundActivityManager;
}
