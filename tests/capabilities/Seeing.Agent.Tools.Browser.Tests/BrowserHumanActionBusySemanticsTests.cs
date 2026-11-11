using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 「智能体正在操作」遮罩的<b>信号源</b>守门：只有 agent 的工具调用可以点亮它。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须用反射钉住</b>：忙碌态是 <c>BrowserPageHandle</c> 内部私有方法的副作用，
/// 行为差异只体现在「走了哪条私有通道」，编译期完全看不出来。
/// 一旦有人给某个纯 UI 方法顺手写成 <c>RunAsync</c>，测试全绿但界面立刻出问题。
/// </para>
/// <para>
/// <b>真实事故</b>：面板 resize、地址栏导航、工具栏刷新/前进/后退 曾经全走 <c>RunAsync</c>。
/// 后果链条是确定的：刷新页面 → WS 重连 → 前端 <c>onopen</c> 立即 <c>sendResize()</c> →
/// 服务端把 <c>busy</c> 置 1 并推送 → 遮罩盖上。
/// 用户看到「agent 已经不操控了，刷新一下却一直显示智能体在操作」，
/// 而且遮罩会拦截输入、接管按钮被压在下面，表现为「点击接管也不生效」。
/// </para>
/// <para>
/// 判据：<b>凡是用户能在工作台界面上直接触发</b>的动作，都必须走 <c>RunHumanAsync</c>
/// （或其包装）；<c>RunAsync</c> 只留给 agent 的工具路径。
/// </para>
/// </remarks>
public class BrowserHumanActionBusySemanticsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);

    /// <summary>人工通道方法（名字以 AsUser 结尾）与它们对应的 agent 通道。</summary>
    public static TheoryData<string, string> HumanVsAgentPairs => new()
    {
        { "NavigateAsUserAsync", "NavigateAsync" },
        { "BackAsUserAsync", "BackAsync" },
        { "ForwardAsUserAsync", "ForwardAsync" },
        { "ReloadAsUserAsync", "ReloadAsync" }
    };

    [Theory]
    [MemberData(nameof(HumanVsAgentPairs))]
    public void UserFacingMethod_ShouldBeMarkedAsHumanChannel(string human, string agent)
    {
        // 两个方法都必须存在：改名时这里会立刻失败，迫使作者想清"它属于哪条通道"
        HumanChannel(human).Should().NotBeNull($"工作台人工动作 {human} 必须存在");
        AgentChannel(agent).Should().NotBeNull($"agent 工具路径 {agent} 必须存在");
    }

    [Theory]
    [MemberData(nameof(HumanVsAgentPairs))]
    public void UserFacingMethod_ShouldNotDelegateToRunAsync(string human, string agent)
    {
        // 关键判据：人工方法不能把 RunAsync 当作实现。它们必须经由 RunHumanAsync，
        // 否则用户自己的操作会点亮「智能体正在操作」。
        var body = ReadMethodBodySource(human);

        body.Should().NotBeNull($"应能读到 {human} 的源码（源码守门，见类注释）");
        body.Should().Contain("RunHumanAsync",
            $"{human} 由用户在界面上触发，必须走人工通道，否则会错误点亮忙碌遮罩");
        body.Should().NotContain("=> RunAsync",
            $"{human} 不得走 agent 通道");
    }

    [Fact]
    public void ResizeAsync_ShouldBeHumanChannel()
    {
        // 最典型的一条：resize 完全由面板尺寸决定，是纯 UI 动作。
        // 它曾经走 RunAsync，直接导致「刷新后一直显示智能体在操作」。
        var body = ReadMethodBodySource("ResizeAsync");

        body.Should().NotBeNull();
        body.Should().Contain("RunHumanAsync",
            "视口尺寸由用户拖拽面板决定，不属于 agent 操作；" +
            "走 agent 通道会让每次刷新（WS 重连即 sendResize）都盖上遮罩");
    }

    [Fact]
    public void AgentChannelMethods_ShouldStillUseRunAsync()
    {
        // 反向守门：不能为了修遮罩把 agent 路径也改成人工通道 ——
        // 那会让真正的 agent 操作期间用户能同时操作页面（状态机互相打断）。
        foreach (var name in new[] { "NavigateAsync", "BackAsync", "ForwardAsync", "ReloadAsync" })
        {
            var body = ReadMethodBodySource(name);
            body.Should().NotBeNull();
            body.Should().Contain("RunAsync",
                $"{name} 是 agent 工具路径，必须保留忙碌态（用户不应同时操作页面）");
        }
    }

    // ── helpers ──

    private static MethodInfo? HumanChannel(string name)
        => typeof(BrowserPageHandle).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);

    private static MethodInfo? AgentChannel(string name)
        => typeof(BrowserPageHandle).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// 从源码里抠出某个方法的主体文本。
    /// </summary>
    /// <remarks>
    /// 反射看不到「这个方法内部调了哪个私有通道」，只有源码能看。
    /// 这是有意的形态守门（与本仓 <c>WorkbenchRazorWiringTests</c> 同类做法）：
    /// 编译器不会因为「选了 RunAsync 而不是 RunHumanAsync」报错。
    /// </remarks>
    private static string? ReadMethodBodySource(string methodName)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src", "capabilities", "Seeing.Agent.Tools.Browser", "Engine", "BrowserPageHandle.cs");

        if (!File.Exists(path))
            return null;

        var lines = File.ReadAllLines(path);
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains($" {methodName}(", StringComparison.Ordinal))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
            return null;

        // 方法体到下一个同缩进的 public/internal/private 成员为止（足够覆盖表达式体与块体）
        var builder = new System.Text.StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i > start && IsMemberBoundary(line))
                break;

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static bool IsMemberBoundary(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("///", StringComparison.Ordinal))
            return false;

        return line.StartsWith("    public ", StringComparison.Ordinal)
               || line.StartsWith("    internal ", StringComparison.Ordinal)
               || line.StartsWith("    private ", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(CurrentFile())!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（Seeing.Agent.slnx）");
    }

    private static string CurrentFile(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
