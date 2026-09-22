namespace Seeing.Agent.Cli.Services;

/// <summary>解析 CLI 的工作区根目录，供服务启动与 TUI 子进程共用。</summary>
internal static class CliWorkspace
{
    /// <summary>SEEING_WORKSPACE_ROOT（须真实存在）优先，否则取 CLI 的启动目录。</summary>
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            return env;

        return Directory.GetCurrentDirectory();
    }
}
