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
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}