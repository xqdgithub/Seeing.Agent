using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Events;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Client;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using Xunit;

namespace Seeing.Gateway.Client.Tests;

/// <summary>
/// 回归（QQ「(空回复)」缺陷）：桥侧 RunCompleted 提前 return 弃置订阅枚举后，
/// 滞留共享入站队列的陈旧 execution.complete 帧会立刻终止下一轮订阅 → (空回复)。
/// 顺序多轮 submit/subscribe，每轮必须收集到自己的回复。
/// </summary>
public sealed class RemoteSessionClientSequentialExchangeTests
{
    [Fact]
    public async Task SequentialBridgeExchanges_EachRound_ShouldCollectOwnReply()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        await using var server = await FakeGatewayServer.StartAsync(ct);
        await using var connection = new WebSocketGatewayClient(
            Options.Create(new GatewayClientOptions
            {
                BaseUrl = server.BaseUrl,
                WebSocketPath = "/ws",
                Timeout = TimeSpan.FromSeconds(30)
            }));
        await connection.ConnectAsync(
            "qq-test",
            new Seeing.Agent.Abstractions.Channels.ChannelCapabilities { SupportsReplyAnchor = true },
            ct);
        var client = new RemoteSessionClient(connection);

        var first = await CollectReplyAsync(client, "ses", "你是谁", ct);
        var second = await CollectReplyAsync(client, "ses", "好的", ct);
        var third = await CollectReplyAsync(client, "ses", "帮我查下明天的天气", ct);

        first.Should().Be("reply-1");
        second.Should().Be("reply-2");
        third.Should().Be("reply-3");
    }

    /// <summary>与 QQChannelBridge.ProcessMessageAsync 同构：RunCompleted 即提前 return（弃置订阅枚举）。</summary>
    private static async Task<string?> CollectReplyAsync(
        RemoteSessionClient client, string sessionId, string text, CancellationToken ct)
    {
        var request = new GatewayRequest
        {
            SessionId = sessionId,
            Input = [new GatewayTextContentPart(text)]
        };
        var submit = await client.SubmitAsync(request, ct);
        if (!submit.Success)
            return null;

        var reply = new GatewayAssistantReplyCollector();
        await foreach (var gatewayEvent in client.SubscribeAsync(sessionId, submit.RequestId, ct))
        {
            if (reply.Apply(gatewayEvent) == GatewayReplyDisposition.RunCompleted)
                return reply.Text; // 提前 return：真实桥在此弃置剩余帧
        }
        return reply.Text; // 非终态收尾（桥侧发 (空回复) 的路径）
    }

    /// <summary>
    /// 最小 Kestrel WS 伪服务端：精确模拟 GatewayWebSocketHandler.HandleSubmitAsync 帧编排
    /// —— SubmitAck（带 frame.Id）→ 轮次 ChatEvent 帧（带 frame.Id）→ ExecutionComplete（带 frame.Id）。
    /// </summary>
    private sealed class FakeGatewayServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _submitCount;

        public string BaseUrl { get; private set; } = "http://127.0.0.1:0";

        private FakeGatewayServer(WebApplication app)
        {
            _app = app;
        }

        public static async Task<FakeGatewayServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();

            FakeGatewayServer? server = null;
            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                await server!.HandleConnectionAsync(ws, context.RequestAborted);
            });

            server = new FakeGatewayServer(app);
            await app.StartAsync(ct);
            server.BaseUrl = app.Services
                .GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return server;
        }

        private async Task HandleConnectionAsync(WebSocket ws, CancellationToken ct)
        {
            await SendFrameAsync(ws, GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.Connected, payload: new GatewayConnectedPayload()), ct);

            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(message.ToArray());
                var frame = GatewayWsFrameSerializer.Deserialize(json);
                if (frame?.Type == GatewayWsFrameType.Submit)
                    await HandleSubmitAsync(ws, frame, ct);
                // channel.register / ping 等忽略
            }
        }

        private async Task HandleSubmitAsync(WebSocket ws, GatewayWsFrame frame, CancellationToken ct)
        {
            var request = frame.Payload?.Deserialize<GatewayRequest>(GatewayWsFrameSerializer.JsonOptions);
            var sessionId = request?.SessionId ?? "ses";
            var n = Interlocked.Increment(ref _submitCount);
            var loopId = $"loop-{n}";

            // ① ack（与 HandleSubmitAsync 同序：先 ack 再跑轮次）
            await SendFrameAsync(ws, GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.SubmitAck, frame.Id,
                new GatewaySubmitAckPayload { SessionId = sessionId, Success = true }), ct);

            // ② 模拟执行轮次（LoopStart → delta → LoopComplete），全部带 frame.Id 关联
            await Task.Delay(80, ct);
            await SendChatEventAsync(ws, frame.Id, new LoopStartEvent
            {
                SessionId = sessionId, LoopId = loopId, UserInput = "input"
            }, ct);
            await SendChatEventAsync(ws, frame.Id, new StreamDeltaEvent
            {
                SessionId = sessionId, LoopId = loopId, ContentDelta = $"reply-{n}"
            }, ct);
            await SendChatEventAsync(ws, frame.Id, new LoopCompleteEvent
            {
                SessionId = sessionId, LoopId = loopId, Success = true
            }, ct);

            // ③ 流收口 → execution.complete（桥已提前 return，本帧滞留客户端共享队列）
            await Task.Delay(50, ct);
            await SendFrameAsync(ws, GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.ExecutionComplete, frame.Id,
                new GatewayExecutionCompletePayload { SessionId = sessionId, LoopId = loopId }), ct);
        }

        private static async Task SendChatEventAsync(
            WebSocket ws, string? id, IMessageEvent messageEvent, CancellationToken ct)
        {
            if (!GatewayEventMapper.TryMap(messageEvent, out var gatewayEvent) || gatewayEvent == null)
                throw new InvalidOperationException($"Fake server: unmapped event {messageEvent.Type}");
            await SendFrameAsync(ws, GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.ChatEvent, id, gatewayEvent), ct);
        }

        private static Task SendFrameAsync(WebSocket ws, GatewayWsFrame frame, CancellationToken ct) =>
            SendFrameAsync(ws, GatewayWsFrameSerializer.Serialize(frame), ct);

        private static async Task SendFrameAsync(WebSocket ws, string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
