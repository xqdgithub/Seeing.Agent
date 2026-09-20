namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>在途请求票据：请求标识 + 归属会话，供 WaitAsync 定位与归属校验。</summary>
public readonly record struct RequestTicket(string RequestId, string SessionId);
