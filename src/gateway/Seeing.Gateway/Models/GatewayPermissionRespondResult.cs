namespace Seeing.Gateway.Models;

/// <summary>
/// 权限响应结果
/// </summary>
public record GatewayPermissionRespondResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public static GatewayPermissionRespondResult Ok() => new() { Success = true };

    public static GatewayPermissionRespondResult Fail(string error) => new() { Success = false, Error = error };
}
