using System.Diagnostics;
using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 在宿主操作系统的文件管理器中定位文件（仅本机部署有意义）。
/// <para>
/// 远程/容器部署时打开的是服务器桌面：调用方应先看 <see cref="CanReveal"/>，
/// 不要假装成功。
/// </para>
/// </summary>
public sealed class LocalFileRevealService : IFileRevealService
{
    /// <inheritdoc />
    public bool CanReveal =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <inheritdoc />
    public Task RevealAsync(string absolutePath, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("需要绝对路径。", nameof(absolutePath));

        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            var isFile = File.Exists(absolutePath);
            var info = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            info.Arguments = isFile
                ? $"/select,\"{absolutePath}\""
                : $"\"{absolutePath}\"";
            Process.Start(info);
            return Task.CompletedTask;
        }

        if (OperatingSystem.IsMacOS())
        {
            Start("open", "-R", absolutePath);
            return Task.CompletedTask;
        }

        var target = File.Exists(absolutePath)
            ? Path.GetDirectoryName(absolutePath) ?? absolutePath
            : absolutePath;
        Start("xdg-open", target);
        return Task.CompletedTask;
    }

    private static void Start(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        Process.Start(info);
    }
}
