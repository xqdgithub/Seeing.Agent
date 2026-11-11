namespace Seeing.Agent.Abstractions.Ui.Workbench;

/// <summary>
/// 工作台宿主请求 kind 的唯一命名来源。
/// <para>
/// 两类 kind：
/// ① <b>注册表动作</b> — <c>workbench.registry.{open|list}</c>，由会话页的工作台停靠宿主处理；
/// ② <b>工作台自有动作</b> — <c>workbench.&lt;id&gt;.&lt;action&gt;</c>，由该工作台实例处理。
/// </para>
/// <para>
/// <c>registry</c> 是保留 id，不能作为工作台 id 声明（否则注册表动作会被某个工作台抢走）。
/// </para>
/// </summary>
public static class WorkbenchRequestKinds
{
    /// <summary>kind 命名空间根。</summary>
    public const string Root = "workbench";

    /// <summary>注册表作用域前缀（最长前缀匹配用）。</summary>
    public const string RegistryPrefix = "workbench.registry";

    /// <summary>保留的工作台 id：注册表自身。</summary>
    public const string ReservedRegistryId = "registry";

    /// <summary>
    /// 已合并的历史工作台 id：等价于 <c>files</c> + <c>openPayload.preview=true</c>。
    /// <para>保留它是为了不打破模型既有的 <c>open_workbench(workbenchId=markdown)</c> 调用习惯。</para>
    /// </summary>
    public const string LegacyMarkdownId = "markdown";

    /// <summary>打开工作台。payload：<c>{ workbenchId, dock?, openPayload? }</c>。</summary>
    public const string RegistryOpen = "workbench.registry.open";

    /// <summary>列出当前可打开的工作台。payload：无。</summary>
    public const string RegistryList = "workbench.registry.list";

    /// <summary>请求关闭某个工作台实例。payload：<c>{ dock, workbenchId }</c>。</summary>
    public const string RegistryClose = "workbench.registry.close";

    /// <summary>拼装工作台自有动作 kind。</summary>
    /// <param name="workbenchId">工作台 id（<see cref="WorkbenchContribution.Id"/>）。</param>
    /// <param name="action">动作名（由工作台自己定义）。</param>
    public static string For(string workbenchId, string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbenchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        return $"{Root}.{workbenchId}.{action}";
    }

    /// <summary>该 kind 是否属于注册表作用域（<c>workbench.registry</c> 及其子路径）。</summary>
    public static bool IsRegistryKind(string? kind)
        => kind is not null
           && (string.Equals(kind, RegistryPrefix, StringComparison.Ordinal)
               || kind.StartsWith(RegistryPrefix + ".", StringComparison.Ordinal));

    /// <summary>
    /// 解析工作台自有动作 kind。<c>workbench.registry.*</c> 与不含 action 的 kind 一律解析失败。
    /// </summary>
    /// <param name="kind">待解析 kind。</param>
    /// <param name="workbenchId">工作台 id。</param>
    /// <param name="action">动作名。</param>
    public static bool TryParse(string? kind, out string workbenchId, out string action)
    {
        workbenchId = string.Empty;
        action = string.Empty;

        if (kind is null || !kind.StartsWith(Root + ".", StringComparison.Ordinal))
            return false;

        if (IsRegistryKind(kind))
            return false;

        var rest = kind[(Root.Length + 1)..];
        var dot = rest.IndexOf('.');
        if (dot <= 0 || dot == rest.Length - 1)
            return false;

        workbenchId = rest[..dot];
        action = rest[(dot + 1)..];
        return !string.IsNullOrWhiteSpace(workbenchId) && !string.IsNullOrWhiteSpace(action);
    }

    /// <summary>拼装注册表动作 kind。</summary>
    public static string Registry(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        return $"{RegistryPrefix}.{action}";
    }
}
