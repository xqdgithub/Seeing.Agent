namespace Seeing.Agent.Cli.Services;

/// <summary>转发给 seeing-tui 的启动选项（仅透传显式指定项，未指定时保留 TUI 自身默认）。</summary>
/// <param name="Continue">继续最近的会话</param>
/// <param name="Resume">恢复指定会话 Id</param>
/// <param name="Agent">覆盖默认 Agent</param>
/// <param name="Model">覆盖默认模型</param>
/// <param name="LogLevel">日志级别</param>
/// <param name="Boot">进程启动能力集（写入 SEEING_BOOT）</param>
public sealed record TuiForwardOptions(
    bool Continue = false,
    string? Resume = null,
    string? Agent = null,
    string? Model = null,
    string? LogLevel = null,
    string? Boot = null);

/// <summary>TUI 子进程的启动规则：程序集名、参数与环境。</summary>
internal static class TuiLaunch
{
    /// <summary>与 Seeing.Agent.Tui.csproj 的 AssemblyName 一致。</summary>
    public const string DllName = "seeing-tui.dll";

    public static string[] BuildArguments(TuiForwardOptions options)
    {
        var args = new List<string>();
        if (options.Continue)
        {
            // 用显式 `=true`：无值开关会被子进程 Host 的命令行配置提供程序消费掉紧跟其后的参数。
            args.Add("--continue=true");
        }

        AddValue(args, "--resume", options.Resume);
        AddValue(args, "--agent", options.Agent);
        AddValue(args, "--model", options.Model);
        AddValue(args, "--log-level", options.LogLevel);
        AddValue(args, "--boot", options.Boot);

        return args.ToArray();
    }

    /// <summary>
    /// 子进程环境：工作区经 SEEING_WORKSPACE_ROOT 注入（TUI 的 WorkspaceProvider 以此为准），
    /// 内容根用 DOTNET_CONTENTROOT（TUI 走通用 Host，不识别 ASPNETCORE_CONTENTROOT）。
    /// </summary>
    public static Dictionary<string, string?> BuildEnvironment(string workspaceRoot, string? contentRoot = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SEEING_WORKSPACE_ROOT"] = workspaceRoot,
        };

        if (!string.IsNullOrWhiteSpace(contentRoot))
            env["DOTNET_CONTENTROOT"] = contentRoot;

        return env;
    }

    /// <summary>
    /// 已被重定向的输入无法为 TUI 提供按键，重定向的输出无法承载全屏重绘；
    /// 与 TUI 自身校验（TuiChatEngine.RunAsync 同时检查 stdin/stdout）保持一致，
    /// 避免多拉起一个必然失败退出的子进程。
    /// </summary>
    public static bool CanRunInteractive(bool inputRedirected, bool outputRedirected)
        => !(inputRedirected || outputRedirected);

    private static void AddValue(List<string> args, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        args.Add(name);
        args.Add(value.Trim());
    }
}
