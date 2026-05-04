namespace Seeing.Agent.MCP.Validation;

using Seeing.Agent.MCP.Core;

public static class McpConfigValidator
{
    public static McpValidationResult Validate(McpServerConfig config)
    {
        var errors = new List<McpErrorInfo>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Name))
        {
            errors.Add(McpErrorInfo.ConfigInvalid("name", "名称不能为空"));
        }
        else if (config.Name.Length > 64)
        {
            errors.Add(McpErrorInfo.ConfigInvalid("name", "名称长度不能超过 64 字符"));
        }

        if (config.TransportType == McpTransportType.Stdio)
        {
            if (string.IsNullOrWhiteSpace(config.Command))
            {
                errors.Add(McpErrorInfo.ConfigInvalid("command", "stdio 传输需要配置 command"));
            }
            else
            {
                if (!IsCommandAvailable(config.Command))
                {
                    warnings.Add($"命令可能不存在于 PATH: {config.Command}");
                }
            }
        }
        else
        {
            if (config.Url == null)
            {
                errors.Add(McpErrorInfo.ConfigInvalid("url", "HTTP 传输需要配置 url"));
            }
            else if (!Uri.TryCreate(config.Url.ToString(), UriKind.Absolute, out _))
            {
                errors.Add(McpErrorInfo.ConfigInvalid("url", "URL 格式无效"));
            }
        }

        if (config.ConnectionTimeoutSeconds < 1 || config.ConnectionTimeoutSeconds > 300)
        {
            warnings.Add($"连接超时建议在 1-300 秒之间，当前: {config.ConnectionTimeoutSeconds}");
        }

        if (config.MaxReconnectionAttempts < 0 || config.MaxReconnectionAttempts > 20)
        {
            warnings.Add($"最大重连次数建议在 0-20 之间，当前: {config.MaxReconnectionAttempts}");
        }

        if (errors.Count > 0)
        {
            return McpValidationResult.Invalid(errors[0]);
        }

        if (warnings.Count > 0)
        {
            return McpValidationResult.WithWarnings(warnings);
        }

        return McpValidationResult.Valid();
    }

    private static bool IsCommandAvailable(string command)
    {
        if (Path.IsPathRooted(command) && File.Exists(command))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
            return pathDirs?.Any(d =>
                File.Exists(Path.Combine(d, command)) ||
                File.Exists(Path.Combine(d, command + ".exe"))) ?? false;
        }

        var unixPathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
        return unixPathDirs?.Any(d => File.Exists(Path.Combine(d, command))) ?? false;
    }
}