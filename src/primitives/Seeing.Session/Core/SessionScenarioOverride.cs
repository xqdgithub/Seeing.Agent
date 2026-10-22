namespace Seeing.Session.Core;

/// <summary>
/// 可选的会话级模块/工具裁剪（只能收窄进程级已启用集；结算见 ExecutionRunner）。
/// </summary>
public sealed class SessionScenarioOverride
{
    /// <summary>会话级模块覆盖；null = 使用 sessionScenario base。</summary>
    public SessionModulesOverride? Modules { get; set; }

    /// <summary>会话级工具裁剪。</summary>
    public SessionToolsOverride? Tools { get; set; }

    /// <summary>
    /// 深拷贝（列表复制新容器；脏 JSON 中 <c>null</c> 列表按空处理）。
    /// </summary>
    public SessionScenarioOverride Clone() => new()
    {
        Modules = Modules is null
            ? null
            : new SessionModulesOverride
            {
                Enabled = Modules.Enabled is null ? null : new List<string>(Modules.Enabled)
            },
        Tools = Tools is null
            ? null
            : new SessionToolsOverride
            {
                Disabled = Tools.Disabled is null ? [] : new List<string>(Tools.Disabled),
                PolicyMode = Tools.PolicyMode
            }
    };
}

/// <summary>会话级 modules 覆盖（对应结算式 <c>session.modules.enabled</c>）。</summary>
public sealed class SessionModulesOverride
{
    /// <summary>显式启用列表；null = 使用 sessionScenario.modules ∩ 进程级 enabled。</summary>
    public List<string>? Enabled { get; set; }
}

/// <summary>会话级 tools 裁剪（对应结算式 <c>session.tools.disabled</c>）。</summary>
public sealed class SessionToolsOverride
{
    /// <summary>禁用的工具 id。</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>
    /// 工具暴露策略模式覆盖（会话级；四级优先级最高层）。
    /// 以字符串承载（原语层不依赖 Abstractions 枚举）：<c>All</c>/<c>Auto</c>/<c>Tiered</c>，
    /// 大小写不敏感；null = 未覆盖。解析推迟到读取处，非法值记 Warning 后回退下一级。
    /// </summary>
    public string? PolicyMode { get; set; }
}
