using System.CommandLine;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class InstallCommand
{
    public static Command Create()
    {
        var command = new Command("install", "将已发布的 seeing-cli 链接到全局目录并加入 PATH");

        command.SetAction(_ =>
        {
            try
            {
                var source = AppContext.BaseDirectory;
                var globalDir = InstallService.GetGlobalDir();
                Directory.CreateDirectory(globalDir);
                var link = InstallService.GetLinkPath(globalDir);

                if (OperatingSystem.IsWindows())
                {
                    if (Directory.Exists(link))
                    {
                        Directory.Delete(link);
                        Console.WriteLine($"已清除旧链接: {link}");
                    }

                    InstallService.CreateDirectoryJunction(link, source);
                    Console.WriteLine($"已创建目录联接: {link} -> {source}");

                    var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
                    if (!InstallService.IsPathPresent(userPath, link))
                    {
                        var next = InstallService.AppendPath(userPath, link);
                        Environment.SetEnvironmentVariable("PATH", next, EnvironmentVariableTarget.User);
                        Console.WriteLine($"已将 {link} 加入用户 PATH");
                    }
                    else
                    {
                        Console.WriteLine($"PATH 已包含 {link}");
                    }
                }
                else
                {
                    if (File.Exists(link))
                    {
                        File.Delete(link);
                        Console.WriteLine($"已清除旧链接: {link}");
                    }

                    var exePath = Path.Combine(source, "seeing-cli");
                    InstallService.CreateExecutableSymlink(link, exePath);
                    Console.WriteLine($"已创建符号链接: {link} -> {exePath}");

                    var rc = InstallService.GetShellRcPath();
                    var line = $"export PATH=\"{globalDir}:$PATH\"";
                    var content = File.Exists(rc) ? File.ReadAllText(rc) : string.Empty;
                    if (!content.Contains(line, StringComparison.Ordinal))
                    {
                        File.AppendAllText(rc, Environment.NewLine + line);
                        Console.WriteLine($"已在 {rc} 追加 PATH 导出");
                    }
                    else
                    {
                        Console.WriteLine($"PATH 已写入 {rc}");
                    }
                }

                Console.WriteLine("安装完成。请重启终端（或刷新 PATH）后直接使用 seeing-cli。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"安装失败: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }
}