using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Core;

/// <summary>
/// ToolContext Sink 接线适配器：将委托形式的 Emit/SetMetadata 包装为接口出口，
/// 同一实例同时实现 IToolEventSink 与 IToolMetadataSink。
/// </summary>
internal sealed class ToolSinkAdapter : IToolEventSink, IToolMetadataSink
{
    private readonly Func<IMessageEvent, ValueTask> _emit;
    private readonly Action<string, Dictionary<string, object>?>? _setMetadata;

    /// <summary>
    /// 创建适配器实例
    /// </summary>
    /// <param name="emit">事件推送委托（必填）</param>
    /// <param name="setMetadata">元数据回写委托（可空，未接线时为 no-op）</param>
    public ToolSinkAdapter(
        Func<IMessageEvent, ValueTask> emit,
        Action<string, Dictionary<string, object>?>? setMetadata)
    {
        _emit = emit;
        _setMetadata = setMetadata;
    }

    /// <inheritdoc/>
    public ValueTask EmitAsync(IMessageEvent evt) => _emit(evt);

    /// <inheritdoc/>
    public void SetMetadata(string key, Dictionary<string, object>? value)
        => _setMetadata?.Invoke(key, value);
}