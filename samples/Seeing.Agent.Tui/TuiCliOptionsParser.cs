using System.CommandLine;

namespace Seeing.Agent.Tui;

/// <summary>
/// TUI 启动参数。
/// </summary>
/// <param name="Workspace">工作区路径（可选；经 SEEING_WORKSPACE_ROOT 注入）</param>
/// <param name="Continue">继续最近会话</param>
/// <param name="Resume">恢复指定会话 Id</param>
/// <param name="Agent">覆盖默认 Agent</param>
/// <param name="Model">覆盖默认模型</param>
/// <param name="LogLevel">日志级别（写入文件）</param>
public sealed record TuiCliOptions(
    string? Workspace = null,
    bool Continue = false,
    string? Resume = null,
    string? Agent = null,
    string? Model = null,
    string? LogLevel = null)
{
    /// <summary>解析命令行参数。</summary>
    public static TuiCliOptions Parse(string[] args, TextWriter? error = null)
    {
        string? workspace = null;
        var continueOption = false;
        string? resume = null;
        string? agent = null;
        string? model = null;
        string? logLevel = null;

        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "工作区路径（默认当前目录）",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var continueOpt = new Option<bool>("--continue", "-c") { Description = "继续最近的会话" };
        var resumeOpt = new Option<string?>("--resume") { Description = "恢复指定会话 Id" };
        var agentOpt = new Option<string?>("--agent") { Description = "覆盖默认 Agent" };
        var modelOpt = new Option<string?>("--model") { Description = "覆盖默认模型" };
        var logOpt = new Option<string?>("--log-level") { Description = "日志级别（Debug/Information/Warning/Error）" };

        // --boot 由 BootOverrideSource.ApplyToServices 直接消费原始 args（写入 ProcessSettlementOptions.BootOverride）。
        // 此处仅声明以令解析器接受该选项、避免「未知选项」把其它选项一并丢弃；有意不映射到 TuiCliOptions。
        var bootOpt = new Option<string?>("--boot") { Description = "进程启动能力天花板覆盖（由 BootOverrideSource 消费）" };

        var root = new RootCommand("Seeing.Agent 终端入口（seeing-tui）")
        {
            workspaceArg,
            continueOpt,
            resumeOpt,
            agentOpt,
            modelOpt,
            logOpt,
            bootOpt,
        };

        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            var stderr = error ?? Console.Error;
            stderr.WriteLine("启动参数解析失败，已忽略全部启动参数：");
            foreach (var parseError in parseResult.Errors)
                stderr.WriteLine($"  {parseError.Message}");
            return new TuiCliOptions();
        }

        workspace = parseResult.GetValue(workspaceArg);
        continueOption = parseResult.GetValue(continueOpt);
        resume = parseResult.GetValue(resumeOpt);
        agent = parseResult.GetValue(agentOpt);
        model = parseResult.GetValue(modelOpt);
        logLevel = parseResult.GetValue(logOpt);

        return new TuiCliOptions(workspace, continueOption, resume, agent, model, logLevel);
    }
}
