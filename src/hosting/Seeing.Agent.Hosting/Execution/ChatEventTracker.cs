using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using System.Text.Json;
using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 聊天事件跟踪器 - 跟踪事件并更新 Session 数据
/// <para>
/// 用于将 IMessageEvent 增量更新到 SessionData.Messages
/// </para>
/// </summary>
internal class ChatEventTracker
{
    public const string SchemaSnapshotMetadataKey = "schema_snapshot";

    private SessionMessage? _currentAssistantMessage;
    private string? _currentLoopId;
    private int _currentStep;
    private Dictionary<string, object>? _pendingSchemaSnapshot;

    /// <summary>
    /// 应用事件到 Session
    /// </summary>
    public void ApplyEvent(SessionData session, IMessageEvent evt)
    {
        switch (evt)
        {
            case SchemaSnapshotEvent schemaSnapshot:
                _pendingSchemaSnapshot = BuildSchemaSnapshotPayload(schemaSnapshot);
                // 挂到会话末条消息 metadata（正常路径即刚写入的 user）；禁止每轮都新增空 system 气泡
                AttachSchemaSnapshotMetadata(session, _pendingSchemaSnapshot);
                break;

            case LoopStartEvent loopStart:
                _currentLoopId = loopStart.LoopId;
                _currentAssistantMessage = null;
                _currentStep = 0;
                break;

            case StreamStartEvent streamStart:
                // 同一 (LoopId, Step) 重发 = 该轮重置：仅移除同一轮的未完成 assistant 消息，避免误删上一步已完成消息
                RemovePartialAssistantForTurn(session, streamStart.LoopId, streamStart.Step);
                if (!string.IsNullOrEmpty(streamStart.LoopId))
                    _currentLoopId = streamStart.LoopId;
                _currentStep = streamStart.Step;
                _currentAssistantMessage = null;
                break;

            case StreamDeltaEvent streamDelta:
                EnsureAssistantMessage(session, evt.SessionId);
                if (_currentAssistantMessage != null)
                {
                    if (!string.IsNullOrEmpty(streamDelta.ContentDelta))
                        _currentAssistantMessage.Content += streamDelta.ContentDelta;
                    if (!string.IsNullOrEmpty(streamDelta.ReasoningDelta))
                        _currentAssistantMessage.ReasoningContent =
                            (_currentAssistantMessage.ReasoningContent ?? string.Empty) + streamDelta.ReasoningDelta;
                }
                break;

            case StreamCompleteEvent streamComplete:
                if (streamComplete.Message != null &&
                    !string.Equals(streamComplete.Message.Role, ChatRole.Tool, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureAssistantMessage(session, evt.SessionId);
                    if (_currentAssistantMessage != null)
                    {
                        if (!string.IsNullOrEmpty(streamComplete.Message.Content))
                            _currentAssistantMessage.Content = streamComplete.Message.Content;
                        if (!string.IsNullOrEmpty(streamComplete.Message.ReasoningContent))
                            _currentAssistantMessage.ReasoningContent = streamComplete.Message.ReasoningContent;
                        if (!string.IsNullOrEmpty(streamComplete.Message.ReasoningSignature))
                            _currentAssistantMessage.ReasoningSignature = streamComplete.Message.ReasoningSignature;

                        if (streamComplete.Message.ToolCalls is { Count: > 0 })
                        {
                            _currentAssistantMessage.ToolCalls ??= new List<SessionToolCall>();
                            foreach (var tc in streamComplete.Message.ToolCalls)
                            {
                                if (string.IsNullOrEmpty(tc.Id))
                                    continue;
                                if (_currentAssistantMessage.ToolCalls.Exists(t => t.Id == tc.Id))
                                    continue;
                                _currentAssistantMessage.ToolCalls.Add(new SessionToolCall
                                {
                                    Id = tc.Id,
                                    Name = tc.Name,
                                    Arguments = string.IsNullOrWhiteSpace(tc.Function?.Arguments)
                                        ? "{}"
                                        : tc.Function!.Arguments,
                                    Status = "pending"
                                });
                            }
                        }
                    }
                }
                break;

            case ToolCallEvent toolCall:
                EnsureAssistantMessage(session, evt.SessionId);
                if (_currentAssistantMessage == null)
                    break;

                _currentAssistantMessage.ToolCalls ??= new List<SessionToolCall>();

                var toolCallId = toolCall.ToolCallId ?? Guid.NewGuid().ToString("N");
                var existing = _currentAssistantMessage.ToolCalls.Find(t => t.Id == toolCallId);
                if (existing == null)
                {
                    existing = new SessionToolCall
                    {
                        Id = toolCallId,
                        Name = toolCall.ToolName ?? string.Empty,
                        Arguments = FormatArguments(toolCall.Arguments)
                    };
                    _currentAssistantMessage.ToolCalls.Add(existing);
                }

                existing.Status = toolCall.Status switch
                {
                    ToolCallStatus.Pending => "pending",
                    ToolCallStatus.Running => "running",
                    ToolCallStatus.Success => "success",
                    ToolCallStatus.Failed => "failed",
                    ToolCallStatus.Rejected => "rejected",
                    ToolCallStatus.Cancelled => "cancelled",
                    _ => existing.Status
                };
                if (!string.IsNullOrEmpty(toolCall.Output))
                    existing.Result = toolCall.Output;
                if (toolCall.Error != null)
                    existing.Error = toolCall.Error;
                if (toolCall.Title != null)
                    existing.Title = toolCall.Title;
                if (toolCall.Metadata is { Count: > 0 })
                    existing.Metadata = new Dictionary<string, object>(toolCall.Metadata);
                if (toolCall.Duration is { } duration)
                    existing.DurationMs = duration.TotalMilliseconds;
                break;

            case ErrorEvent:
                // 运行时观测，不进对话消息/LLM 历史；由 ErrorEvent 事件驱动 UI 与执行记录
                break;

            case LoopCancelledEvent:
                // 同上：取消态由 LoopCancelledEvent + ExecutionStatus.Cancelled 表达
                break;

            case ModeUpdateEvent modeUpdate:
                session.SelectedAcpMode = modeUpdate.ModeId;
                break;
        }
    }

    /// <summary>
    /// 获取当前助手消息
    /// </summary>
    public SessionMessage? GetCurrentAssistantMessage() => _currentAssistantMessage;

    /// <summary>
    /// 获取当前 LoopId
    /// </summary>
    public string? GetCurrentLoopId() => _currentLoopId;

    /// <summary>
    /// 清除缓存
    /// </summary>
    public void Clear()
    {
        _currentAssistantMessage = null;
        _currentLoopId = null;
        _currentStep = 0;
        _pendingSchemaSnapshot = null;
    }

    /// <summary>
    /// 若当前累积的 assistant 消息属于同一轮（LoopId+Step），移除之（重试重置）。
    /// 跨步（step 递增）时保留上一步已完成的消息。
    /// </summary>
    private void RemovePartialAssistantForTurn(SessionData session, string? loopId, int step)
    {
        if (_currentAssistantMessage != null)
        {
            var sameStep = _currentAssistantMessage.Step == step;
            var sameLoop = string.IsNullOrEmpty(loopId)
                || string.IsNullOrEmpty(_currentAssistantMessage.LoopId)
                || string.Equals(_currentAssistantMessage.LoopId, loopId, StringComparison.Ordinal);
            if (sameStep && sameLoop)
            {
                session.RemoveMessage(_currentAssistantMessage);
                _currentAssistantMessage = null;
            }
            return;
        }

        // 仅在有明确 LoopId 时按 Id 兜底移除，避免 null LoopId 误删历史轮次
        if (string.IsNullOrEmpty(loopId))
            return;
        var expectedId = string.Format("{0}_step{1}", loopId, step);
        session.RemoveMessages(m => m.Id == expectedId);
    }

    private void EnsureAssistantMessage(SessionData session, string sessionId)
    {
        if (_currentAssistantMessage != null)
            return;

        _currentAssistantMessage = SessionMessage.AssistantMessage(string.Empty);
        var loopPrefix = _currentLoopId ?? sessionId;
        _currentAssistantMessage.Id = string.Format("{0}_step{1}", loopPrefix, _currentStep);
        _currentAssistantMessage.Step = _currentStep;
        _currentAssistantMessage.LoopId = _currentLoopId;
        if (_pendingSchemaSnapshot != null)
        {
            _currentAssistantMessage.Metadata ??= new Dictionary<string, object>();
            _currentAssistantMessage.Metadata[SchemaSnapshotMetadataKey] =
                DeepCloneObject(_pendingSchemaSnapshot);
        }

        session.AddMessage(_currentAssistantMessage);
    }

    private static void AttachSchemaSnapshotMetadata(SessionData session, Dictionary<string, object> payload)
    {
        // 优先挂在当前会话末条（正常路径即刚写入的 user）；无消息时才建空 system 载体（由 UI/历史过滤隐藏）
        SessionMessage target;
        if (session.Messages.Count > 0)
        {
            target = session.Messages[^1];
            target.Metadata ??= new Dictionary<string, object>();
        }
        else
        {
            target = SessionMessage.SystemMessage(string.Empty);
            target.Metadata = new Dictionary<string, object>();
            session.AddMessage(target);
        }

        target.Metadata[SchemaSnapshotMetadataKey] = DeepCloneObject(payload);
    }

    private static Dictionary<string, object> BuildSchemaSnapshotPayload(SchemaSnapshotEvent snapshot)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["executionId"] = snapshot.ExecutionId,
            ["toolIds"] = snapshot.ToolIds.ToList(),
            ["sectionIds"] = snapshot.SectionIds.ToList()
        };
    }

    internal static object DeepCloneObject(object value) => value switch
    {
        null => null!,
        string s => s,
        IDictionary<string, object> dict => DeepCloneDictionary(dict),
        IReadOnlyDictionary<string, object> rod => DeepCloneDictionary(rod),
        IList<object> list => list.Select(DeepCloneObject).ToList(),
        IList<string> strings => strings.ToList(),
        IEnumerable<string> enumerable when value is not string => enumerable.ToList(),
        ICloneable cloneable => cloneable.Clone(),
        _ => value
    };

    private static Dictionary<string, object> DeepCloneDictionary(IEnumerable<KeyValuePair<string, object>> source)
    {
        var clone = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, val) in source)
            clone[key] = DeepCloneObject(val);
        return clone;
    }

    private static string FormatArguments(object? arguments)
    {
        if (arguments == null)
            return "{}";

        if (arguments is string str)
            return str;

        return JsonSerializer.Serialize(arguments);
    }
}
