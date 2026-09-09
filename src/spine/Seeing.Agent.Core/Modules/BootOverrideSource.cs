using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 解析进程 Boot 覆盖：CLI <c>--boot</c> / 环境变量 <c>SEEING_BOOT</c>。
/// 优先级：命令行参数 &gt; 环境变量。
/// </summary>
public static class BootOverrideSource
{
    /// <summary>环境变量名。</summary>
    public const string EnvironmentVariableName = "SEEING_BOOT";

    /// <summary>CLI 长选项名（含前缀）。</summary>
    public const string ArgumentName = "--boot";

    /// <summary>从环境变量读取；空白则 null。</summary>
    public static string? ResolveFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// 从参数列表解析 <c>--boot value</c> 或 <c>--boot=value</c>；空白则 null。
    /// </summary>
    public static string? ResolveFromArgs(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
            return null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            if (arg.StartsWith("--boot=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--boot=".Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            if (string.Equals(arg, ArgumentName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                    return null;
                var value = args[i + 1]?.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>命令行优先，其次环境变量。</summary>
    public static string? Resolve(IReadOnlyList<string>? args = null) =>
        ResolveFromArgs(args) ?? ResolveFromEnvironment();

    /// <summary>
    /// 将解析到的 Boot 写入已登记的 <see cref="ProcessSettlementOptions"/> 单例（若存在）。
    /// 应在 Host Shape 登记 <see cref="ProcessSettlementOptions"/> 之后调用。
    /// </summary>
    public static IServiceCollection ApplyToServices(
        IServiceCollection services,
        IReadOnlyList<string>? args = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var boot = Resolve(args);
        if (string.IsNullOrWhiteSpace(boot))
            return services;

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(ProcessSettlementOptions))
                continue;

            if (descriptor.ImplementationInstance is ProcessSettlementOptions instance)
            {
                instance.BootOverride = boot;
                return services;
            }
        }

        // 尚未登记时直接登记一份仅含 BootOverride 的选项（少见；正常路径应先 AddSeeingHosting*）
        services.AddSingleton(new ProcessSettlementOptions { BootOverride = boot });
        return services;
    }
}
