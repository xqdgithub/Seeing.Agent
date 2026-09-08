namespace Seeing.Gateway.QQ;

/// <summary>QQ Channel 健康快照。</summary>
public sealed record QQChannelHealthSnapshot(
    bool Enabled,
    bool WsConnected,
    DateTimeOffset? LastReadyAt,
    int IdentifyFailCount,
    string Status,
    string Detail);

public sealed class QQChannelHealth
{
    private readonly QQOptions _options;
    private readonly Connection.QQWebSocketClient _ws;

    public QQChannelHealth(
        Microsoft.Extensions.Options.IOptions<QQOptions> options,
        Connection.QQWebSocketClient ws)
    {
        _options = options.Value;
        _ws = ws;
    }

    public QQChannelHealthSnapshot GetSnapshot()
    {
        if (!_options.Enabled)
        {
            return new QQChannelHealthSnapshot(
                false, false, null, 0, "disabled", "QQ channel is disabled.");
        }

        var connected = _ws.IsConnected;
        var issues = new List<string>();
        if (!connected)
            issues.Add("WebSocket is not connected");

        return new QQChannelHealthSnapshot(
            Enabled: true,
            WsConnected: connected,
            LastReadyAt: _ws.LastReadyAt,
            IdentifyFailCount: _ws.IdentifyFailCount,
            Status: issues.Count == 0 ? "healthy" : "unhealthy",
            Detail: issues.Count == 0
                ? "QQ WebSocket is active."
                : string.Join("; ", issues));
    }
}
