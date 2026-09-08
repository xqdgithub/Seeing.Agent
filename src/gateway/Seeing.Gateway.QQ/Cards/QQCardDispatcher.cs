using System.Text.Json;

namespace Seeing.Gateway.QQ.Cards;

/// <summary>可注册的 QQ 交互卡片种类。</summary>
public interface IQQCardKind
{
    string Name { get; }

    /// <summary>interaction action.data 前缀，例如 <c>seeing_perm:</c>。</summary>
    string ActionDataPrefix { get; }

    /// <summary>尝试处理 INTERACTION_CREATE；返回是否已认领。</summary>
    Task<bool> TryHandleInteractionAsync(JsonElement d, CancellationToken cancellationToken);
}

/// <summary>按 action.data 前缀路由交互事件。</summary>
public sealed class QQCardDispatcher
{
    private readonly IReadOnlyList<IQQCardKind> _kinds;

    public QQCardDispatcher(IEnumerable<IQQCardKind> kinds) =>
        _kinds = kinds.ToList();

    public async Task<bool> TryHandleInteractionAsync(JsonElement d, CancellationToken cancellationToken)
    {
        var buttonData = ExtractButtonData(d);
        if (string.IsNullOrEmpty(buttonData))
            return false;

        foreach (var kind in _kinds)
        {
            if (!buttonData.StartsWith(kind.ActionDataPrefix, StringComparison.Ordinal))
                continue;
            return await kind.TryHandleInteractionAsync(d, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    internal static string? ExtractButtonData(JsonElement d)
    {
        if (d.TryGetProperty("data", out var data)
            && data.TryGetProperty("resolved", out var resolved)
            && resolved.TryGetProperty("button_data", out var bd))
            return bd.GetString();

        if (d.TryGetProperty("button_data", out var direct))
            return direct.GetString();

        return null;
    }
}
