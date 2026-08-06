using System.Diagnostics;

namespace Seeing.Agent.Cli.Services;

public static class InstallService
{
    public static string ResolveGlobalDir(bool isWindows, string home)
        => isWindows
            ? Path.Combine(home, "bin")
            : Path.Combine(home, ".local", "bin");

    public static string GetGlobalDir()
        => ResolveGlobalDir(
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string GetLinkPath(string globalDir)
        => Path.Combine(globalDir, "seeing-cli");

    public static bool IsPathPresent(string? path, string dir)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalized = dir.TrimEnd(Path.DirectorySeparatorChar);
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(e => string.Equals(
                e.Trim().TrimEnd(Path.DirectorySeparatorChar),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    public static string AppendPath(string? existing, string dir)
    {
        var entry = dir.TrimEnd(Path.DirectorySeparatorChar);
        return string.IsNullOrEmpty(existing)
            ? entry
            : existing.TrimEnd(Path.PathSeparator) + Path.PathSeparator + entry;
    }

    public static string GetShellRcPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "";
        return shell.EndsWith("zsh", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(home, ".zshrc")
            : Path.Combine(home, ".bashrc");
    }

    public static void CreateDirectoryJunction(string linkPath, string sourceDir)
    {
        var psi = new ProcessStartInfo(
            "cmd.exe",
            $"/c mklink /J \"{linkPath}\" \"{sourceDir.TrimEnd(Path.DirectorySeparatorChar)}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        process!.WaitForExit();
        var error = process.StandardError.ReadToEnd();

        if (!Directory.Exists(linkPath))
            throw new InvalidOperationException($"创建目录联接失败: {error.Trim()}");
    }

    public static void CreateExecutableSymlink(string linkPath, string exePath)
        => File.CreateSymbolicLink(linkPath, exePath);
}