using System.Threading.Channels;

namespace Seeing.Agent.Tui.Input;

public enum TuiInputAction
{
    None,
    InsertText,
    InsertNewline,
    Submit,
    Backspace,
    DeleteForward,
    MoveLeft,
    MoveRight,
    HistoryPrev,
    HistoryNext,
    Complete,
    Cancel,
    ExitRequested,
}

/// <param name="IsEscape">是否由 Esc 产生（用于「执行中取消需二次确认」的区分；Ctrl+C 为 false）。</param>
public readonly record struct TuiKeyInput(TuiInputAction Action, string Text = "", bool IsEscape = false);

public abstract record TuiRawInput;

public sealed record TuiRawText(string Value) : TuiRawInput;

public sealed record TuiRawKey(ConsoleKey Key, char KeyChar, bool Ctrl, bool Shift, bool Alt) : TuiRawInput;

public sealed record TuiRawPasteStart : TuiRawInput;

public sealed record TuiRawPasteEnd : TuiRawInput;

public sealed record TuiRawEscape(string Sequence) : TuiRawInput;

/// <summary>
/// 原始输入源（raw stdin）。退出时由 <see cref="StopAsync"/> 停止读取线程。
/// </summary>
/// <remarks>提示（权限/问答）期间靠 <c>TuiPromptInputRelay</c> 分流按键，不做读取挂起。</remarks>
public interface IRawInputSource : IAsyncDisposable
{
    ChannelReader<TuiRawInput> Reader { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
