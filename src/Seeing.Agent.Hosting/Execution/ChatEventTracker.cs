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
                // 立即落一条带 metadata 的消息，保证 Fork / 落盘可重建（不依赖后续 Stream）
                var snapshotMessage = SessionMessage.SystemMessage(string.Empty);
                snapshotMessage.Metadata = new Dictionary<string, object>
                {
                    [SchemaSnapshotMetadataKey] = DeepCloneObject(_pendingSchemaSnapshot)
                };
                session.AddMessage(snapshotMessage);
                break;

            case LoopStartEvent loopStart:
                _currentLoopId = loopStart.LoopId;
                _currentAssistantMessage = null;
                _currentStep = 0;
                break;

            case StreamStartEvent streamStart:
                if (!string.IsNullOrEmpty(streamStart.LoopId))
                    _currentLoopId = streamStart.LoopId;
                var step = streamStart.Step;
                if (step > 0 || _currentAssistantMessage == null)
                {
                    _currentStep = step;
                    _currentAssistantMessage = null;
                }
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
                if (toolCall.Output != null)
                    existing.Result = toolCall.Output;
                if (toolCall.Error != null)
                    existing.Error = toolCall.Error;
                break;

            case ErrorEvent error:
                session.AddMessage(SessionMessage.SystemMessage($"错误: {error.Message}"));
                break;

            case LoopCancelledEvent cancelled:
                session.AddMessage(SessionMessage.SystemMessage($"对话已取消: {cancelled.Reason}"));
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
