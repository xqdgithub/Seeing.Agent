using FluentAssertions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Rendering.Prompts;
using Seeing.Agent.Tui.Tests.Fakes;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Threading.Channels;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// <see cref="QuestionPrompt"/> 端到端回归：单选/多选走自绘 <see cref="TuiListPrompt"/>（键盘 + 鼠标），
/// 文本/自定义续接走 Spectre <see cref="TextPrompt{T}"/> + TUI 按键通道。
/// 覆盖两类曾导致「引擎整体退出」的缺陷：markup 注入（<c>[ ]</c>）与 TextPrompt 忽略 Esc，
/// 并断言迁移前后<b>业务结果不变</b>。
/// </summary>
public sealed class QuestionPromptTests
{
    /// <summary>
    /// 假底锚的绝对行 = 富样式帧末行（底部键位提示行）。无描述、2 选项时帧共 6 行
    /// （题目/空行/第1项/第2项/空行/提示），第 2 项在倒数第 3 行 → 绝对行 = <see cref="AnchorRow"/> - 2。
    /// </summary>
    private const int AnchorRow = 20;

    [Fact]
    public async Task ShowAsync_Single_WithMarkupLikeText_ShouldNotThrowAndPreserveRawAnswer()
    {
        var request = new QuestionRequest
        {
            Id = "req1",
            SessionId = "ses1",
            Questions =
            [
                new Question
                {
                    Id = "q1",
                    Header = "城市 [列表]",
                    QuestionText = "要查哪个城市? [[注意]]",
                    Kind = QuestionKind.Single,
                    Options =
                    [
                        new QuestionOption { Label = "[北京]" },
                        new QuestionOption { Label = "上海" },
                    ],
                },
            ],
        };

        var (surface, source) = CreateSurface();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Status.Should().Be(QuestionResultStatus.Completed);
        result.Answers.Single().SelectedLabels.Should().Equal("[北京]");
    }

    [Fact]
    public async Task ShowAsync_Text_WithMarkupLikeTitleAndDefault_ShouldNotThrow()
    {
        var request = new QuestionRequest
        {
            Id = "req2",
            SessionId = "ses1",
            Questions =
            [
                new Question
                {
                    Id = "q1",
                    Header = "城市 [必填]",
                    QuestionText = "要查哪个城市? [例如 北京]",
                    Kind = QuestionKind.Text,
                    DefaultCustomAnswer = "[默认] 北京",
                },
            ],
        };

        var (surface, source) = CreateSurface();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Answers.Single().CustomAnswer.Should().Be("[默认] 北京");
    }

    [Fact]
    public async Task ShowAsync_Text_WhenEscapePressed_ShouldReturnNull()
    {
        // TextPrompt 自身忽略 Esc；由输入层 EscCancellationScope 取消令牌后收敛为 null。
        var request = TextQuestion();
        var (surface, source) = CreateSurface();

        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Cancel));

        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ShowAsync_Single_WhenEscapePressed_ShouldReturnNull()
    {
        var request = new QuestionRequest
        {
            Id = "req4",
            SessionId = "ses1",
            Questions =
            [
                new Question
                {
                    Id = "q1",
                    QuestionText = "选一个",
                    Kind = QuestionKind.Single,
                    Options = [new QuestionOption { Label = "a" }, new QuestionOption { Label = "b" }],
                },
            ],
        };

        var (surface, source) = CreateSurface();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Cancel));

        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ShowAsync_Text_WithCjkAnswer_ShouldReturnRawText()
    {
        var request = TextQuestion();
        var (surface, source) = CreateSurface();

        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "北京"));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        result!.Answers.Single().CustomAnswer.Should().Be("北京");
    }

    [Fact]
    public async Task ShowAsync_Single_WhenSecondOptionMovedAndSubmitted_ShouldReturnSecondLabel()
    {
        // Arrange：键盘 ↓ + Enter 选中第二项。
        var request = SingleRequest(allowCustom: false, required: true);
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.HistoryNext));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Answers.Single().SelectedLabels.Should().Equal("b");
    }

    [Fact]
    public async Task ShowAsync_Single_WhenMouseClickedItem_ShouldReturnClickedLabel()
    {
        // Arrange：Left-Press 命中第二项（底锚行 20 → 第二项在倒数第 3 行 → 行 18）。
        var request = SingleRequest(allowCustom: false, required: true);
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(Mouse(TuiMouseButton.Left, TuiMousePhase.Press, AnchorRow - 2));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Answers.Single().SelectedLabels.Should().Equal("b");
    }

    [Fact]
    public async Task ShowAsync_Single_WhenMouseHoveredThenEnter_ShouldReturnHoveredLabel()
    {
        // Arrange：hover 移动游标到第二项（不确认），Release 被忽略，Enter 确认。
        var request = SingleRequest(allowCustom: false, required: true);
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(Mouse(TuiMouseButton.None, TuiMousePhase.Motion, AnchorRow - 2));
        source.Writer.TryWrite(Mouse(TuiMouseButton.Left, TuiMousePhase.Release, AnchorRow - 2));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Answers.Single().SelectedLabels.Should().Equal("b");
    }

    [Fact]
    public async Task ShowAsync_Single_WhenMouseDisabledChannelNull_ShouldConvergeToCancel()
    {
        // Arrange：无按键通道 → 控件按取消收敛（不决策）。
        var request = SingleRequest(allowCustom: false, required: true);
        var surface = new FakeTerminalSurface { ListKeys = null };

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ShowAsync_Single_WhenOtherChoiceSelected_ShouldContinueWithCustomText()
    {
        // Arrange：↓ ↓ 移到「其他（自定义）」，Enter 后续文本提示。
        var request = SingleRequest(allowCustom: true, required: true);
        var (surface, source) = CreateSurface();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.HistoryNext));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.HistoryNext));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "广州"));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert：OtherChoice 不进 SelectedLabels，仅填 CustomAnswer。
        var answer = result!.Answers.Single();
        answer.SelectedLabels.Should().BeEmpty();
        answer.CustomAnswer.Should().Be("广州");
    }

    [Fact]
    public async Task ShowAsync_Single_WithoutOptions_ShouldFallbackToTextAnswer()
    {
        // Arrange：无任何选项 → 与迁移前一致，回落文本答案。
        var request = new QuestionRequest
        {
            Id = "reqEmpty",
            SessionId = "ses1",
            Questions =
            [
                new Question { Id = "q1", QuestionText = "自由回答", Kind = QuestionKind.Single },
            ],
        };
        var (surface, source) = CreateSurface();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "随便"));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Answers.Single().CustomAnswer.Should().Be("随便");
    }

    [Fact]
    public async Task ShowAsync_Multiple_WhenTwoToggledAndSubmitted_ShouldReturnBothLabels()
    {
        // Arrange：Space 切换 ☐/☑，↓ 移动游标，Enter 提交勾选集。
        var request = MultipleRequest(allowCustom: false, required: true);
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, " "));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.HistoryNext));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, " "));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Answers.Single().SelectedLabels.Should().Equal("a", "b");
    }

    [Fact]
    public async Task ShowAsync_Multiple_WhenMouseClickedItem_ShouldToggleAndSubmitChecked()
    {
        // Arrange：Left-Press 命中第二项 → 切换勾选（多选不直接提交），Enter 提交。
        var request = MultipleRequest(allowCustom: false, required: true);
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(Mouse(TuiMouseButton.Left, TuiMousePhase.Press, AnchorRow - 2));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Answers.Single().SelectedLabels.Should().Equal("b");
    }

    [Fact]
    public async Task ShowAsync_Multiple_WhenRequiredAndEmptySubmitted_ShouldBlockThenAcceptChecked()
    {
        // Arrange：必填时空提交被控件拦截（不退出），Space + Enter 后才收敛。
        var request = MultipleRequest(allowCustom: false, required: true);
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, " "));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Answers.Single().SelectedLabels.Should().Equal("a");
    }

    [Fact]
    public async Task ShowAsync_Multiple_WhenNotRequiredAndEmptySubmitted_ShouldReturnEmptyLabels()
    {
        // Arrange：非必填空提交 → 返回空 SelectedLabels（与迁移前 NotRequired 语义一致）。
        var request = MultipleRequest(allowCustom: false, required: false);
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Status.Should().Be(QuestionResultStatus.Completed);
        result.Answers.Single().SelectedLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task ShowAsync_Multiple_WhenDefaultSelectedLabelsPreChecked_ShouldSubmitWithoutKeys()
    {
        // Arrange：DefaultSelectedLabels=["a"] 映射为预勾选下标 0，直接 Enter 即带出。
        var request = MultipleRequest(allowCustom: false, required: true);
        request.Questions[0].DefaultSelectedLabels = ["a"];
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result!.Answers.Single().SelectedLabels.Should().Equal("a");
    }

    [Fact]
    public async Task ShowAsync_Multiple_WhenOtherChoiceChecked_ShouldFillCustomAnswerAndKeepLabels()
    {
        // Arrange：默认勾选 a；↓ ↓ 到「其他（自定义）」→ Space 勾选 → Enter → 续文本。
        var request = MultipleRequest(allowCustom: true, required: true);
        request.Questions[0].DefaultSelectedLabels = ["a"];
        var (surface, source) = CreateSurface();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.HistoryNext));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.HistoryNext));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, " "));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "补充"));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert：OtherChoice 不进 SelectedLabels，自定义文本进 CustomAnswer。
        var answer = result!.Answers.Single();
        answer.SelectedLabels.Should().Equal("a");
        answer.CustomAnswer.Should().Be("补充");
    }

    [Fact]
    public async Task ShowAsync_Multiple_WhenCustomTextEscaped_ShouldReturnNull()
    {
        // Arrange：勾选「其他」后自定义文本被 Esc → 整体收敛为不决策。
        var request = MultipleRequest(allowCustom: true, required: true);
        var (surface, source) = CreateSurface();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.HistoryNext));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.HistoryNext));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, " "));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Cancel));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ShowAsync_Multiple_WhenEscapePressed_ShouldReturnNull()
    {
        // Arrange：多选列表按 Esc → null（不决策），与旧 AddCancelResult 语义等价。
        var request = MultipleRequest(allowCustom: false, required: true);
        var surface = CreateListSurface(out var source);
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Cancel));

        // Act
        var result = await QuestionPrompt.ShowAsync(surface, request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeNull();
    }

    private static QuestionRequest SingleRequest(bool allowCustom, bool required)
        => new()
        {
            Id = "reqSingle",
            SessionId = "ses1",
            Questions =
            [
                new Question
                {
                    Id = "q1",
                    QuestionText = "选一个",
                    Kind = QuestionKind.Single,
                    AllowCustom = allowCustom,
                    Required = required,
                    Options = [new QuestionOption { Label = "a" }, new QuestionOption { Label = "b" }],
                },
            ],
        };

    private static QuestionRequest MultipleRequest(bool allowCustom, bool required)
        => new()
        {
            Id = "reqMultiple",
            SessionId = "ses1",
            Questions =
            [
                new Question
                {
                    Id = "q1",
                    QuestionText = "多选",
                    Kind = QuestionKind.Multiple,
                    AllowCustom = allowCustom,
                    Required = required,
                    Options = [new QuestionOption { Label = "a" }, new QuestionOption { Label = "b" }],
                },
            ],
        };

    private static QuestionRequest TextQuestion() => new()
    {
        Id = "req3",
        SessionId = "ses1",
        Questions =
        [
            new Question { Id = "q1", Header = "城市", QuestionText = "要查哪个城市?", Kind = QuestionKind.Text },
        ],
    };

    private static TuiKeyInput Mouse(TuiMouseButton button, TuiMousePhase phase, int row)
        => new(TuiInputAction.Mouse, Mouse: new TuiRawMouse(button, phase, Col: 1, Row: row));

    /// <summary>
    /// 列表（键盘/鼠标）测试端口替身：FakeTerminalSurface + 预置按键流 + 即时配对底锚（模拟「发一个收一个」的 DSR）。
    /// </summary>
    private static FakeTerminalSurface CreateListSurface(out Channel<TuiKeyInput> source)
    {
        source = Channel.CreateUnbounded<TuiKeyInput>();
        return new FakeTerminalSurface
        {
            ListKeys = source.Reader,
            ListAnchor = new FakeAnchorProbe(),
            ListMouseEnabled = true,
        };
    }

    /// <summary>
    /// 端口替身：把提示委托直接跑在「按键通道驱动」的控制台上，
    /// 并复用生产接线 <see cref="EscapeCancellationScope"/>（Esc → 取消令牌，仅文本路径需要）。
    /// 列表路径经 <see cref="ITerminalSurface.PromptListAsync{T}"/> 直读同一按键通道（键盘用例，鼠标关闭）。
    /// </summary>
    private static (ITerminalSurface Surface, Channel<TuiKeyInput> Source) CreateSurface()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var input = new ChannelAnsiConsoleInput();
        input.Bind(source.Reader, CancellationToken.None);

        var inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(new StringWriter()),
        });

        return (new ChannelSurface(new InputOverrideConsole(inner, input), input, source), source);
    }

    /// <summary>模拟 DSR「发一个收一个」：BeginProbe 后即时配对底锚行，跨帧代次仍由 TryGet 携带。</summary>
    private sealed class FakeAnchorProbe : ITuiAnchorProbe
    {
        private long _gen;
        private long _pairedGen;

        public long BeginProbe(Action writeDsr)
        {
            var gen = Interlocked.Increment(ref _gen);
            writeDsr();
            Volatile.Write(ref _pairedGen, gen);
            return gen;
        }

        public void BeginProbe(long gen, Action writeDsr)
        {
            Volatile.Write(ref _pairedGen, gen);
        }

        public void Report(int row) { /* 底锚行固定为 AnchorRow（与测试几何约定一致）。 */ }

        public bool TryGet(out int row, out long gen)
        {
            row = AnchorRow;
            gen = Volatile.Read(ref _pairedGen);
            return gen != 0;
        }

        public void Invalidate() => Volatile.Write(ref _pairedGen, 0);
    }

    private sealed class ChannelSurface : ITerminalSurface
    {
        private readonly ChannelAnsiConsoleInput _input;
        private readonly Channel<TuiKeyInput> _source;

        public ChannelSurface(IAnsiConsole console, ChannelAnsiConsoleInput input, Channel<TuiKeyInput> source)
        {
            Console = console;
            _input = input;
            _source = source;
        }

        public IAnsiConsole Console { get; }

        public Task UpdateAsync(IRenderable view, TuiCaret? caret = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task CommitAsync(IRenderable committed, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<T> PromptAsync<T>(Func<IAnsiConsole, CancellationToken, Task<T>> prompt, CancellationToken ct = default)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var escapeScope = EscapeCancellationScope.Attach(_input, cancellation);
            return await prompt(Console, cancellation.Token).WaitAsync(cancellation.Token).ConfigureAwait(false);
        }

        public Task<T> PromptListAsync<T>(Func<TuiPromptContext, CancellationToken, Task<T>> prompt, CancellationToken ct = default)
            => PromptAsync((console, promptCt) => prompt(
                new TuiPromptContext
                {
                    Console = console,
                    Keys = _source.Reader,
                    MouseEnabled = false,
                    Width = 120,
                },
                promptCt), ct);

        public Task StopAsync() => Task.CompletedTask;
    }
}
