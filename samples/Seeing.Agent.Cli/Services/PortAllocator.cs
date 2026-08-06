using System.Net;
using System.Net.Sockets;

namespace Seeing.Agent.Cli.Services;

public sealed class PortAllocator
{
    public int NextAvailable(int preferred, int maxAttempts = 100)
    {
        for (var port = preferred; port < preferred + maxAttempts; port++)
        {
            if (!IsInUse(port)) return port;
        }

        throw new InvalidOperationException(
            $"在 {preferred}~{preferred + maxAttempts - 1} 范围内找不到可用端口");
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