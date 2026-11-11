using System.Text;

namespace Seeing.Agent.Tools.Browser;

/// <summary>快照里的单个可交互元素。</summary>
/// <param name="Ref">元素引用（<c>e1</c>、<c>e2</c>…），页面内唯一。</param>
/// <param name="Role">角色（link / button / textbox / checkbox …）。</param>
/// <param name="Name">可读名（aria-label / placeholder / 关联 label / value / 文本）。</param>
/// <param name="States">状态标记（如 <c>disabled</c>、<c>checked</c>）。</param>
public sealed record BrowserSnapshotElement(
    string Ref,
    string Role,
    string Name,
    IReadOnlyList<string> States)
{
    /// <summary>渲染成快照的一行。</summary>
    public string ToLine()
    {
        var builder = new StringBuilder(64);
        builder.Append(Ref).Append(' ').Append(Role);

        if (Name.Length > 0)
            builder.Append(" \"").Append(Name).Append('"');

        foreach (var state in States)
            builder.Append(" (").Append(state).Append(')');

        return builder.ToString();
    }
}

/// <summary>页面快照（一次采集的稳定结果）。</summary>
/// <param name="Url">页面地址。</param>
/// <param name="Title">页面标题。</param>
/// <param name="RefEpoch">采集时的导航代次；用于判定 ref 是否已失效。</param>
/// <param name="Elements">可交互元素（按 DOM 顺序）。</param>
/// <param name="TotalElements">页面中可交互元素总数（可能大于 <paramref name="Elements"/> 数量）。</param>
/// <param name="VisibleText">可见文本摘要（可空）。</param>
public sealed record BrowserSnapshot(
    string Url,
    string Title,
    int RefEpoch,
    IReadOnlyList<BrowserSnapshotElement> Elements,
    int TotalElements,
    string? VisibleText)
{
    /// <summary>是否因上限被截断。</summary>
    public bool Truncated => TotalElements > Elements.Count;

    /// <summary>
    /// 渲染成给模型看的文本。
    /// <para>
    /// 格式稳定（顺序固定、无随机成分）：同一页面同一时刻多次采集必须逐字节相等，
    /// 否则上下文增量管道会误判为「页面变了」。
    /// </para>
    /// </summary>
    public string Render()
    {
        var builder = new StringBuilder(512);

        builder.Append("页面：").AppendLine(string.IsNullOrWhiteSpace(Title) ? "(无标题)" : Title);
        builder.Append("URL：").AppendLine(Url);

        if (Elements.Count == 0)
        {
            builder.AppendLine(TotalElements == 0
                ? "可交互元素：无"
                : $"可交互元素：共 {TotalElements} 个，但都不适合交互（已跳过不可见项）");
        }
        else
        {
            builder.Append("可交互元素 ").Append(Elements.Count).Append(" 个：").AppendLine();
            builder.AppendLine();
            foreach (var element in Elements)
                builder.AppendLine(element.ToLine());

            if (Truncated)
                builder.AppendLine($"(另有 {TotalElements - Elements.Count} 个元素未列出；可用 browser_eval 精确定位)");
        }

        if (!string.IsNullOrWhiteSpace(VisibleText))
        {
            builder.AppendLine();
            builder.AppendLine("可见文本摘要：");
            builder.AppendLine(VisibleText);
        }

        return builder.ToString().TrimEnd();
    }
}
