using System.Net;
using System.Net.Sockets;

namespace Seeing.Agent.Cli.Services;

public sealed class PortAllocator
{
    public int NextAvailable(int preferred, int maxAttempts = 100)
    {
        ValidatePort(preferred);
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        for (var offset = 0; offset < maxAttempts; offset++)
        {
            var candidate = (long)preferred + offset;
            if (candidate > IPEndPoint.MaxPort) break;

            if (IsAvailable((int)candidate)) return (int)candidate;
        }

        var lastPort = (int)Math.Min(
            IPEndPoint.MaxPort,
            (long)preferred + maxAttempts - 1);
        throw new InvalidOperationException(
            $"在 {preferred}~{lastPort} 范围内找不到可用端口");
    }

    /// <summary>
    /// 检查端口是否可以被 WebUI 绑定。该检查与实际启动之间仍可能存在竞态，
    /// 因此调用方还必须处理子进程绑定失败的情况。
    /// </summary>
    public bool IsAvailable(int port)
    {
        ValidatePort(port);
        return !IsInUse(port);
    }

    private static void ValidatePort(int port)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static bool IsInUse(int port)
    {
        // 覆盖常见地址族：IPv4/IPv6 的通配符与回环。Windows 允许具体地址与通配符
        // 地址在同一端口部分叠加，单一地址探测会漏判真实占用，故逐项探测，
        // 任一地址报"地址已占用"即视为端口被占用。
        var addresses = new[]
        {
            IPAddress.Any,          // 0.0.0.0
            IPAddress.Loopback,     // 127.0.0.1
            IPAddress.IPv6Any,      // ::
            IPAddress.IPv6Loopback  // ::1
        };
        return addresses.Any(addr => !CanBind(addr, port));
    }

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex)
        {
            // 仅在明确"地址已被占用"时判定为占用；其余异常（如该地址族不可用）
            // 不能证明端口被占用，按可绑定处理。
            return ex.SocketErrorCode != SocketError.AddressAlreadyInUse;
        }
    }
}