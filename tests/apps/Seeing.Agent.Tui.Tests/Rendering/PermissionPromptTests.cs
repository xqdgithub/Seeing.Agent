using System.Threading.Channels;
using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering.Prompts;
using Seeing.Agent.Tui.Tests.Fakes;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// <see cref="PermissionPrompt"/> 迁移回归：Spectre SelectionPrompt → 自绘 <see cref="TuiListPrompt"/>
/// （经 <c>FakeTerminalSurface.PromptListAsync</c> 注入按键/鼠标通道）。
/// 覆盖：scope 过滤选项集不变、键盘/鼠标同结果、Esc→null（不决策）、Panel 概要先于列表且留空行分隔。
/// </summary>
public sealed class PermissionPromptTests
{
    // 默认 scopes [Once, Session] → 4 项：0 本次允许 / 1 始终允许（本会话） / 2 本次拒绝 / 3 始终拒绝。
    // 帧几何：标题 1 行 + 4 项 = 5 行；item k 的 RowsFromBottom = 5-2-k = 3-k；底锚 30 → 行 27..30。
    private const int AnchorRow = 30;

    [Fact]
    public async Task ShowAsync_DefaultScopes_EnterOnFirst_ShouldReturnAllowOnceViaListSurface()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys, Key(TuiInputAction.Submit));

        var result = await RunAsync(surface, Request(PermissionGrantScope.Once, PermissionGrantScope.Session));

        result.Should().NotBeNull();
        result!.Effect.Should().Be(PermissionEffect.Allow);
        result.Scope.Should().Be(PermissionGrantScope.Once);
        surface.Prompts.Should().ContainSingle().Which.Should().StartWith("list:");
    }

    [Fact]
    public async Task ShowAsync_DefaultScopes_KeyboardToLastItem_ShouldReturnDenySession()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys,
            Key(TuiInputAction.HistoryNext),
            Key(TuiInputAction.HistoryNext),
            Key(TuiInputAction.HistoryNext),
            Key(TuiInputAction.Submit));

        var result = await RunAsync(surface, Request(PermissionGrantScope.Once, PermissionGrantScope.Session));

        result.Should().NotBeNull();
        result!.Effect.Should().Be(PermissionEffect.Deny);
        result.Scope.Should().Be(PermissionGrantScope.Session);
    }

    [Fact]
    public async Task ShowAsync_MouseClickOnSessionAllowRow_ShouldReturnSameResultAsKeyboard()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys, Mouse(TuiMouseButton.Left, TuiMousePhase.Press, row: AnchorRow - 2));

        var result = await RunAsync(surface, Request(PermissionGrantScope.Once, PermissionGrantScope.Session));

        result.Should().NotBeNull();
        result!.Effect.Should().Be(PermissionEffect.Allow);
        result.Scope.Should().Be(PermissionGrantScope.Session);
    }

    [Fact]
    public async Task ShowAsync_EscapePressed_ShouldReturnNull()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys, Key(TuiInputAction.Cancel));

        var result = await RunAsync(surface, Request(PermissionGrantScope.Once, PermissionGrantScope.Session));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ShowAsync_NoKeysChannel_ShouldReturnNullWithoutBlocking()
    {
        var surface = new FakeTerminalSurface { ListKeys = null, ListAnchor = new FakeAnchorProbe(AnchorRow) };

        var result = await RunAsync(surface, Request(PermissionGrantScope.Once));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ShowAsync_ShouldRenderPanelSummaryBeforeListWithBlankSeparator()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys, Key(TuiInputAction.Cancel));

        await RunAsync(surface, Request(PermissionGrantScope.Once, PermissionGrantScope.Session));

        var output = surface.Output;
        var panelPos = output.IndexOf("权限请求", StringComparison.Ordinal);
        var listTitlePos = output.IndexOf("请选择", StringComparison.Ordinal);
        var firstItemPos = output.IndexOf("本次允许", StringComparison.Ordinal);
        panelPos.Should().BeGreaterThanOrEqualTo(0);
        panelPos.Should().BeLessThan(listTitlePos, "Panel 概要必须先于列表写入");
        listTitlePos.Should().BeLessThan(firstItemPos);

        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var titleLine = Array.FindIndex(lines, line => line.Trim() == "请选择");
        titleLine.Should().BeGreaterThan(0);
        lines[titleLine - 1].Trim().Should().BeEmpty("Panel 与列表首行之间应留一行空行分隔");
    }

    [Fact]
    public async Task ShowAsync_OnceOnlyScope_ShouldOfferOnlyOnceAllowAndDeny()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys, Key(TuiInputAction.Cancel));

        await RunAsync(surface, Request(PermissionGrantScope.Once));

        var output = surface.Output;
        output.Should().Contain("本次允许").And.Contain("本次拒绝");
        output.Should().NotContain("始终允许（本会话）").And.NotContain("始终拒绝").And.NotContain("允许此目录（会话目录）");
    }

    [Fact]
    public async Task ShowAsync_SessionOnlyScope_ShouldExcludeOnceAndDirectory_AndConfirmAllowSession()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys, Key(TuiInputAction.Submit));

        var result = await RunAsync(surface, Request(PermissionGrantScope.Session));

        result.Should().NotBeNull();
        result!.Effect.Should().Be(PermissionEffect.Allow);
        result.Scope.Should().Be(PermissionGrantScope.Session);
        var output = surface.Output;
        output.Should().Contain("始终允许（本会话）").And.Contain("本次拒绝").And.Contain("始终拒绝");
        output.Should().NotContain("本次允许");
    }

    [Fact]
    public async Task ShowAsync_SessionDirectoryScope_EnterOnFirst_ShouldReturnAllowSessionDirectory()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys, Key(TuiInputAction.Submit));

        var result = await RunAsync(surface, Request(PermissionGrantScope.SessionDirectory));

        result.Should().NotBeNull();
        result!.Effect.Should().Be(PermissionEffect.Allow);
        result.Scope.Should().Be(PermissionGrantScope.SessionDirectory);
        surface.Output.Should().Contain("允许此目录（会话目录）").And.Contain("本次拒绝");
    }

    [Fact]
    public async Task ShowAsync_EmptyAllowedScopes_ShouldFallbackToOnceAndSessionChoices()
    {
        var (surface, keys) = CreateSurface();
        Feed(keys, Key(TuiInputAction.Cancel));

        await RunAsync(surface, Request());

        var output = surface.Output;
        output.Should().Contain("本次允许").And.Contain("始终允许（本会话）")
            .And.Contain("本次拒绝").And.Contain("始终拒绝");
        output.Should().NotContain("允许此目录（会话目录）");
    }

    private static PermissionRequest Request(params PermissionGrantScope[] scopes) => new()
    {
        SessionId = "ses1",
        PermissionKind = "file.write",
        Resource = "src/app.ts",
        RiskLevel = "high",
        Message = "需要写入文件",
        AllowedScopes = scopes.Length == 0 ? [] : scopes,
    };

    private static (FakeTerminalSurface Surface, Channel<TuiKeyInput> Keys) CreateSurface()
    {
        var keys = Channel.CreateUnbounded<TuiKeyInput>();
        var surface = new FakeTerminalSurface
        {
            ListKeys = keys.Reader,
            ListAnchor = new FakeAnchorProbe(AnchorRow),
        };
        return (surface, keys);
    }

    private static void Feed(Channel<TuiKeyInput> keys, params TuiKeyInput[] inputs)
    {
        foreach (var input in inputs)
            keys.Writer.TryWrite(input).Should().BeTrue();
    }

    private static Task<PermissionPromptResult?> RunAsync(FakeTerminalSurface surface, PermissionRequest request)
        => PermissionPrompt
            .ShowAsync(surface, request, "agent-A", TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static TuiKeyInput Key(TuiInputAction action) => new(action);

    private static TuiKeyInput Mouse(TuiMouseButton button, TuiMousePhase phase, int row)
        => new(TuiInputAction.Mouse, Mouse: new TuiRawMouse(button, phase, Col: 5, Row: row));

    /// <summary>恒校准底锚：TryGet 固定返回 <paramref name="anchorRow"/>（列表末行绝对行），代次随 BeginProbe 递增。</summary>
    private sealed class FakeAnchorProbe(int anchorRow) : ITuiAnchorProbe
    {
        private long _gen;

        public long BeginProbe(Action writeDsr)
        {
            writeDsr();
            return ++_gen;
        }

        public void BeginProbe(long gen, Action writeDsr)
        {
            writeDsr();
            _gen = gen;
        }

        public void Report(int row)
        {
        }

        public bool TryGet(out int row, out long gen)
        {
            row = anchorRow;
            gen = _gen;
            return true;
        }

        public void Invalidate()
        {
        }
    }
}
