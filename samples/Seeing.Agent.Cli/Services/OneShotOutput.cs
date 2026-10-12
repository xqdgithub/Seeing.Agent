using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Cli.Services;

/// <summary>
/// 一次性执行输出策略：默认仅把最终 assistant 文本写 stdout；<c>--stream</c> 时逐行写 JSONL 事件。
/// <para>非 stream 模式下不输出工具进度等噪音，只在失败时写错误/权限诊断到 stderr；stream 模式写全部事件到 stdout。</para>
/// </summary>
public sealed class OneShotOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _stream;

    public OneShotOutput(TextWriter stdout, TextWriter stderr, bool stream)
    {
        _stdout = stdout;
        _stderr = stderr;
        _stream = stream;
    }

    /// <summary>最近一条 stream.complete 的 assistant 文本（默认模式最终输出）。</summary>
    public string? FinalText { get; private set; }

    /// <summary>是否收到过 ErrorEvent。</summary>
    public bool HasError { get; private set; }

    /// <summary>处理一条事件：stream 模式逐行输出 JSONL；否则仅跟踪最终文本（不输出工具进度等噪音）。</summary>
    public void Apply(IMessageEvent evt)
    {
        switch (evt)
        {
            case StreamCompleteEvent complete:
                FinalText = complete.Message.Content;
                break;
            case ErrorEvent error:
                HasError = true;
                if (!_stream)
                    WriteDiagnostic($"[error] {error.Message}");
                break;
            case PermissionResolvedEvent { Decision: PermissionEffect.Deny } denied:
                if (!_stream)
                    WriteDiagnostic($"[permission] 已拒绝 {denied.Reason ?? denied.RequestId}（需 --yolo 或改用 TUI）");
                break;
        }

        if (_stream)
        {
            _stdout.WriteLine(JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions));
            _stdout.Flush();
        }
    }

    /// <summary>输出最终结果：stream 模式已逐行输出，无需再写；默认模式写最终文本。</summary>
    public void WriteFinal()
    {
        if (_stream)
            return;

        if (!string.IsNullOrEmpty(FinalText))
        {
            _stdout.Write(FinalText);
            if (!FinalText.EndsWith('\n'))
                _stdout.WriteLine();
        }

        _stdout.Flush();
    }

    /// <summary>写一条诊断到 stderr。</summary>
    public void WriteDiagnostic(string message)
    {
        _stderr.WriteLine(message);
        _stderr.Flush();
    }
}
