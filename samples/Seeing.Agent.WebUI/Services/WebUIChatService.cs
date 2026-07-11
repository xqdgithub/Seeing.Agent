using Microsoft.Extensions.Logging;
using Seeing.Agent.App;
using Seeing.Agent.App.Models;
using Seeing.Agent.Core.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// WebUI Chat 服务 - 封装 IChatOrchestrator 用于 Blazor 组件
/// </summary>
public class WebUIChatService
{
    private readonly IChatOrchestrator _orchestrator;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<WebUIChatService> _logger;

    public WebUIChatService(
        IChatOrchestrator orchestrator,
        ISessionManager sessionManager,
        ILogger<WebUIChatService> logger)
    {
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// 执行聊天并返回事件流
    /// </summary>
    public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        string sessionId,
        string text,
        List<ChatAttachment>? attachments = null,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = new ChatInput
        {
            Text = text,
            Attachments = attachments
        };

        await foreach (var evt in _orchestrator.ExecuteAsync(sessionId, input, options, cancellationToken))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// 停止执行
    /// </summary>
    public bool Stop(string sessionId) => _orchestrator.Stop(sessionId);

    /// <summary>
    /// 创建新会话
    /// </summary>
    public async Task<SessionData> CreateSessionAsync(string? workingDirectory = null, CancellationToken ct = default)
    {
        var session = _sessionManager.Create();
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            session.WorkingDirectory = workingDirectory;
        }
        await _sessionManager.SaveAsync(session.Id);
        return session;
    }

    /// <summary>
    /// 清空会话消息
    /// </summary>
    public async Task ClearSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var session = _sessionManager.Get(sessionId);
        if (session != null)
        {
            session.Messages.Clear();
            await _sessionManager.SaveAsync(sessionId);
        }
    }
}
