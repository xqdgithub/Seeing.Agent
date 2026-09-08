namespace Seeing.Agent.Acp.Backends;

/// <summary>
/// 规范化 ACP 后端可执行文件路径。配置中应使用绝对路径。
/// </summary>
public static class AcpExecutableResolver
{
    public static string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return command;

        if (Path.IsPathRooted(command))
            return File.Exists(command) ? Path.GetFullPath(command) : command;

        if (command.Contains(Path.DirectorySeparatorChar) ||
            command.Contains(Path.AltDirectorySeparatorChar))
        {
            var candidate = Path.GetFullPath(command);
            if (File.Exists(candidate))
                return candidate;
        }

        return command;
    }
}
