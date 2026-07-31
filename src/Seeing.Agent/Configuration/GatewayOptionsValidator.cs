using Microsoft.Extensions.Options;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// GatewayOptions 配置验证器
    /// </summary>
    public class GatewayOptionsValidator : IValidateOptions<GatewayOptions>
    {
        public ValidateOptionsResult Validate(string? name, GatewayOptions options)
        {
            var errors = new List<string>();

            if (options.Port < 1 || options.Port > 65535)
                errors.Add($"Port 必须在 1-65535 范围内，当前值: {options.Port}");

            if (options.PermissionTimeoutSeconds < 0)
                errors.Add($"PermissionTimeoutSeconds 不能为负数，当前值: {options.PermissionTimeoutSeconds}");

            if (string.IsNullOrEmpty(options.BindAddress))
                errors.Add("BindAddress 不能为空");

            if (options.WebSocketKeepAliveSeconds < 1)
                errors.Add($"WebSocketKeepAliveSeconds 必须大于 0，当前值: {options.WebSocketKeepAliveSeconds}");

            if (options.PermissionMode != "auto_approve" && options.PermissionMode != "interactive")
                errors.Add($"PermissionMode 必须是 auto_approve 或 interactive，当前值: {options.PermissionMode}");

            if (errors.Count > 0)
                return ValidateOptionsResult.Fail(errors);

            return ValidateOptionsResult.Success;
        }
    }
}
