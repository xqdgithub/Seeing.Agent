namespace Seeing.Agent.Cli.Services;

/// <summary>
/// 服务进程启动模式：前台占用当前终端（子进程继承控制台），
/// 或后台脱离终端（输出重定向到日志文件）。
/// </summary>
public enum ServiceRunMode
{
    Foreground,
    Background,
}

public static class ServiceRunModeResolver
{
    /// <summary>
    /// 未显式指定开关时的默认模式：WebUI 默认前台（输出直接出现在 CLI 终端）；
    /// Gateway 默认后台（保持既有行为）。
    /// </summary>
    public static ServiceRunMode DefaultFor(string service)
        => service == "webui" ? ServiceRunMode.Foreground : ServiceRunMode.Background;

    public static bool TryResolve(
        string service,
        bool background,
        bool foreground,
        out ServiceRunMode mode,
        out string? error)
    {
        error = null;
        if (background && foreground)
        {
            mode = DefaultFor(service);
            error = "不能同时指定 --background 与 --foreground";
            return false;
        }

        mode = background
            ? ServiceRunMode.Background
            : foreground
                ? ServiceRunMode.Foreground
                : DefaultFor(service);
        return true;
    }
}
