using System.Text.Json;
using Seeing.Gateway.Client;

namespace Seeing.Gateway.QQ.Cards;

/// <summary>权限确认键盘（原 QQPermissionResponder）。</summary>
public sealed class PermissionCardKind : IQQCardKind
{
    private readonly QQPermissionState _state;
    private readonly WebSocketGatewayClient _gatewayClient;

    public PermissionCardKind(QQPermissionState state, WebSocketGatewayClient gatewayClient)
    {
        _state = state;
        _gatewayClient = gatewayClient;
    }

    public string Name => "permission";
    public string ActionDataPrefix => QQPermissionCardBuilder.ActionPrefix;

    public async Task<bool> TryHandleInteractionAsync(JsonElement d, CancellationToken cancellationToken)
    {
        var buttonData = QQCardDispatcher.ExtractButtonData(d);
        if (!QQPermissionCardBuilder.TryParseAction(buttonData, out var allow, out var requestId))
            return false;

        if (!_state.TryTake(requestId, out var sessionId, out var permissionId))
            return true;

        await _gatewayClient.RespondPermissionAsync(sessionId, permissionId, allow, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}
