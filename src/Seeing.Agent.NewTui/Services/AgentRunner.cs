using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Sessions;
using Seeing.Agent.Llm;
using Seeing.Agent.NewTui.State;

namespace Seeing.Agent.NewTui.Services;

public class AgentRunner
{
    private readonly AgentExecutor _executor;
    private readonly IAgentRegistry _registry;
    private readonly SessionManager _sessions;
    private readonly TuiPermissionChannel _permission;
    private readonly AppState _state;
    private readonly IServiceProvider _services;

    public AgentRunner(
        AgentExecutor executor,
        IAgentRegistry registry,
        SessionManager sessions,
        TuiPermissionChannel permission,
        AppState state,
        IServiceProvider services)
    {
        _executor = executor;
        _registry = registry;
        _sessions = sessions;
        _permission = permission;
        _state = state;
        _services = services;
    }

    public async Task SendMessageAsync(string input, CancellationToken ct = default)
    {
        var processingCt = _state.StartProcessing();

        try
        {
            _state.CurrentSession ??= await _sessions.CreateSessionAsync();

            var userMsg = new ChatMessage { Role = ChatRole.User, Content = input };
            _state.CurrentSession.AddMessage(userMsg);

            var agent = _registry.GetOrCreateAgentInstance(_state.CurrentAgent);
            if (agent == null) throw new InvalidOperationException($"Agent 未找到: {_state.CurrentAgent}");

            var definition = AgentDefinition.FromAgent(agent);

            var context = new AgentContext
            {
                SessionId = _state.CurrentSession.SessionId,
                Services = _services,
                PermissionChannel = _permission,
                History = _state.CurrentSession.Messages.ToList(),
                WorkingDirectory = _state.WorkspaceRoot
            };

            await foreach (var evt in _executor.ExecuteAsync(definition, context, ct))
            {
                HandleEvent(evt);
                if (ct.IsCancellationRequested || processingCt.IsCancellationRequested)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消
        }
        finally
        {
            _state.EndProcessing();
        }
    }

    private void HandleEvent(IMessageEvent evt)
    {
        switch (evt)
        {
            case StreamDeltaEvent delta:
                _state.StreamingContent.Append(delta.ContentDelta);
                break;
            case StreamCompleteEvent complete:
                _state.CurrentSession?.AddMessage(complete.Message);
                _state.StreamingContent.Clear();
                break;
            case ToolCallEvent tool:
                UpdateToolCall(tool);
                break;
            case ErrorEvent error:
                _state.LastError = error.Message;
                break;
        }
        _state.NotifyChanged();
    }

    private void UpdateToolCall(ToolCallEvent tool)
    {
        var existing = _state.ActiveToolCalls.FirstOrDefault(t => t.Id == tool.ToolCallId);
        if (existing == null)
        {
            existing = new ToolCallDisplay
            {
                Id = tool.ToolCallId,
                Name = tool.ToolName,
                Status = tool.Status
            };
            _state.ActiveToolCalls.Add(existing);
        }
        else
        {
            existing.Status = tool.Status;
            existing.Result = tool.Output;
            existing.Error = tool.Error;
            existing.Duration = tool.Duration;
        }
    }
}
