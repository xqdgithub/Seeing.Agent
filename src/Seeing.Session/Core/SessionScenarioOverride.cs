namespace Seeing.Session.Core;

/// <summary>
/// 可选的会话级模块/工具裁剪（只能收窄进程级已启用集；结算见 ExecutionJobService）。
/// </summary>
public sealed class SessionScenarioOverride
{
    /// <summary>会话级模块覆盖；null = 使用 sessionScenario base。</summary>
    public SessionModulesOverride? Modules { get; set; }

    /// <summary>会话级工具裁剪。</summary>
    public SessionToolsOverride? Tools { get; set; }
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
}
