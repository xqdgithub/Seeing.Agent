using System.Threading.Channels;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Tests.Fakes;

/// <summary>
/// 记录调用序列的终端出口替身，供渲染/引擎测试断言使用。
/// </summary>
public sealed class FakeTerminalSurface : ITerminalSurface, IDisposable
{
    private readonly object _gate = new();
    private readonly StringWriter _output = new();
    private readonly List<string> _updates = [];
    private readonly List<string> _committed = [];
    private readonly List<string> _prompts = [];
    private readonly IAnsiConsole _console;

    public FakeTerminalSurface()
    {
        _console = CreateConsole(_output);
    }

    public IAnsiConsole Console => _console;

    public IReadOnlyList<string> Updates => _updates;

    public IReadOnlyList<string> Committed => _committed;

    public IReadOnlyList<string> Prompts => _prompts;

    public string Output => _output.ToString();

    public int UpdateCount => _updates.Count;

    public int CommitCount => _committed.Count;

    public bool Stopped { get; private set; }

    /// <summary>最近一次活动区更新的编辑插入点（无则为 null）。</summary>
    public TuiCaret? LastCaret { get; private set; }

    /// <summary>最近一次活动区更新随帧携带的 DSR 探测代次（0 = 不探测）。</summary>
    public long LastProbeGen { get; private set; }

    public Task UpdateAsync(IRenderable view, TuiCaret? caret = null, CancellationToken ct = default)
        => UpdateAsync(view, caret, probeGen: 0, ct);

    public Task UpdateAsync(IRenderable view, TuiCaret? caret, long probeGen, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _updates.Add(RenderToString(view));
            LastCaret = caret;
            LastProbeGen = probeGen;
        }

        return Task.CompletedTask;
    }

    public Task CommitAsync(IRenderable committed, CancellationToken ct = default)
    {
        var text = RenderToString(committed);
        lock (_gate)
            _committed.Add(text);

        _console.Write(committed);
        return Task.CompletedTask;
    }

    public async Task<T> PromptAsync<T>(Func<IAnsiConsole, CancellationToken, Task<T>> prompt, CancellationToken ct = default)
    {
        lock (_gate)
            _prompts.Add(typeof(T).Name);

        return await prompt(_console, ct).ConfigureAwait(false);
    }

    /// <summary>自绘列表提示读取的按键流（测试注入）；null 时控件仅降级/取消。</summary>
    public ChannelReader<TuiKeyInput>? ListKeys { get; set; }

    /// <summary>自绘列表提示的鼠标开关（默认开，便于测点击/悬停）。</summary>
    public bool ListMouseEnabled { get; set; } = true;

    public async Task<T> PromptListAsync<T>(Func<TuiPromptContext, CancellationToken, Task<T>> prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        lock (_gate)
            _prompts.Add("list:" + typeof(T).Name);

        var ctx = new TuiPromptContext
        {
            Console = _console,
            Keys = ListKeys,
            Anchor = ListAnchor ?? new NullTuiAnchorProbe(),
            MouseEnabled = ListMouseEnabled,
            Width = _console.Profile.Width,
        };
        return await prompt(ctx, ct).ConfigureAwait(false);
    }

    /// <summary>自绘列表提示的底锚（测试注入，用于鼠标命中）。</summary>
    public ITuiAnchorProbe? ListAnchor { get; set; }

    public Task StopAsync()
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    public string RenderToString(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = CreateConsole(writer);
        console.Write(renderable);
        return writer.ToString();
    }

    public void Dispose() => _output.Dispose();

    private static IAnsiConsole CreateConsole(StringWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = 120;
        console.Profile.Height = 40;
        return console;
    }
}
