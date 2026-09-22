using FluentAssertions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Rendering.Prompts;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Threading.Channels;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// <see cref="QuestionPrompt"/> 端到端回归：真实 Spectre 提示 + TUI 按键通道。
/// 覆盖两类曾导致「引擎整体退出」的缺陷：markup 注入（<c>[ ]</c>）与 TextPrompt 忽略 Esc。
/// </summary>
public sealed class QuestionPromptTests
{
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

    private static QuestionRequest TextQuestion() => new()
    {
        Id = "req3",
        SessionId = "ses1",
        Questions =
        [
            new Question { Id = "q1", Header = "城市", QuestionText = "要查哪个城市?", Kind = QuestionKind.Text },
        ],
    };

    /// <summary>
    /// 端口替身：把提示委托直接跑在「按键通道驱动」的控制台上，
    /// 并复用生产接线 <see cref="EscapeCancellationScope"/>（Esc → 取消令牌）。
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

        return (new ChannelSurface(new InputOverrideConsole(inner, input), input), source);
    }

    private sealed class ChannelSurface : ITerminalSurface
    {
        private readonly ChannelAnsiConsoleInput _input;

        public ChannelSurface(IAnsiConsole console, ChannelAnsiConsoleInput input)
        {
            Console = console;
            _input = input;
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

        public Task StopAsync() => Task.CompletedTask;
    }
}
