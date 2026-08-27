using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Decorators;
using Seeing.Agent.Output;
using Seeing.Session.Storage;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

/// <summary>
/// ToolOutputLimiterDecorator 测试 - 统一工具输出限制（超限落盘 + 头尾预览）
/// </summary>
public class ToolOutputLimiterDecoratorTests
{
    private class OutputTool : ITool
    {
        public string Id => "output";
        public string Description => "输出工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public string Result { get; set; } = "";
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true, Output = Result });
    }

    private sealed class SkipTool : OutputTool
    {
    }

    [ToolCapability(ToolCapabilityKeys.OutputSkip, "true")]
    private sealed class DeclaredSkipTool : OutputTool
    {
    }

    [ToolCapability(ToolCapabilityKeys.OutputMaxBytes, "100")]
    private sealed class MaxBytesTool : OutputTool
    {
    }

    private sealed class FailingTool : ITool
    {
        public string Id => "fail";
        public string Description => "失败工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = false, Error = "boom" });
    }

    private static (string TempDir, ToolOutputLimiterDecorator Decorator, SessionWorkspaceWhitelist Whitelist) Create(
        ITool inner, Action<ToolOutputOptions>? configure = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-limiter-" + Guid.NewGuid().ToString("N"));
        var store = new SessionToolOutputStore(new FileSessionStore(tempDir), NullLogger<SessionToolOutputStore>.Instance);
        var whitelist = new SessionWorkspaceWhitelist();

        var opts = new SeeingAgentOptions();
        configure?.Invoke(opts.ToolOutput);
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(opts);

        var decorator = new ToolOutputLimiterDecorator(
            inner,
            options.Object,
            store,
            whitelist,
            NullLogger<ToolOutputLimiterDecorator>.Instance);
        return (tempDir, decorator, whitelist);
    }

    private static ToolContext Ctx(string sessionId = "ses_a", string? callId = "call_1")
        => new() { SessionId = sessionId, CallId = callId, CancellationToken = CancellationToken.None };

    private static async Task<ToolResult> ExecAsync(ITool decorator)
        => await decorator.ExecuteAsync(JsonDocument.Parse("{}").RootElement, Ctx());

    [Fact]
    public async Task SmallOutput_ShouldPassThroughUnchanged()
    {
        var (_, decorator, _) = Create(new OutputTool { Result = "short" });
        var result = await ExecAsync(decorator);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("short");
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task LargeOutput_ShouldSpillToRefDirectory_AndReplaceOutputWithPreview()
    {
        var big = new string('x', 60 * 1024);
        var (tempDir, decorator, whitelist) = Create(new OutputTool { Result = big });
        var result = await ExecAsync(decorator);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("<persisted-output>");
        result.Output.Should().Contain("call_1.txt");
        result.Output.Should().Contain("原始");
        result.Metadata["truncated"].Should().Be(true);
        result.Metadata["outputPath"].Should().Be(Path.Combine(tempDir, "ses_a.ref", "call_1.txt"));
        ((int)result.Metadata["originalBytes"]).Should().BeGreaterThan(50 * 1024);

        File.ReadAllText((string)result.Metadata["outputPath"]).Should().Be(big);
        whitelist.Contains("ses_a", Path.Combine(tempDir, "ses_a.ref", "call_1.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task LargeOutput_ShouldKeepHeadAndTail()
    {
        var head = new string('A', 1024);
        var middle = new string('M', 50 * 1024);
        var tail = new string('Z', 1024);
        var (_, decorator, _) = Create(new OutputTool { Result = head + middle + tail });

        var result = await ExecAsync(decorator);

        result.Output.Should().Contain("省略 51200 字符");
        result.Output.Should().Contain(head);
        result.Output.Should().Contain(tail);
    }

    [Fact]
    public async Task OutputSkipTool_ShouldBypassLimiter()
    {
        var big = new string('x', 60 * 1024);
        var (_, decorator, _) = Create(new DeclaredSkipTool { Result = big });
        var result = await ExecAsync(decorator);

        result.Output.Should().Be(big);
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task OutputMaxBytesTool_ShouldUseToolSpecificThreshold()
    {
        var (_, decorator, _) = Create(new MaxBytesTool { Result = new string('x', 200) });
        var result = await ExecAsync(decorator);

        result.Output.Should().Contain("<persisted-output>");

        var (_, decorator2, _) = Create(new OutputTool { Result = new string('y', 200) });
        var r2 = await ExecAsync(decorator2);
        r2.Output.Should().Be(new string('y', 200));
        r2.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task SpillFailure_ShouldDegradeToInlineTruncated()
    {
        var big = new string('x', 60 * 1024);
        var failingStore = new Mock<IToolOutputStore>();
        failingStore.Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("磁盘满"));
        var whitelist = new SessionWorkspaceWhitelist();
        var opts = new SeeingAgentOptions();
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(opts);
        var decorator = new ToolOutputLimiterDecorator(
            new OutputTool { Result = big },
            options.Object,
            failingStore.Object,
            whitelist,
            NullLogger<ToolOutputLimiterDecorator>.Instance);

        var result = await ExecAsync(decorator);

        result.Output.Should().Contain("<persisted-output>");
        result.Output.Should().Contain("写入会话目录失败");
        result.Metadata["truncated"].Should().Be(true);
        result.Metadata["spillFailed"].Should().Be(true);
    }

    [Fact]
    public async Task FailingResult_ShouldNotBeTouched()
    {
        var (_, decorator, _) = Create(new FailingTool());
        var result = await ExecAsync(decorator);

        result.Success.Should().BeFalse();
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task EmptyOutput_ShouldNotBeTouched()
    {
        var (_, decorator, _) = Create(new OutputTool { Result = "" });
        var result = await ExecAsync(decorator);

        result.Output.Should().Be("");
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task ExactThreshold_ShouldNotSpill()
    {
        var exactly = new string('e', 50 * 1024);
        var (_, decorator, _) = Create(new OutputTool { Result = exactly });
        var result = await ExecAsync(decorator);

        result.Output.Should().Be(exactly);
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task OneByteOverThreshold_ShouldSpill()
    {
        var over = new string('o', 50 * 1024 + 1);
        var (_, decorator, _) = Create(new OutputTool { Result = over });
        var result = await ExecAsync(decorator);

        result.Output.Should().Contain("<persisted-output>");
        result.Metadata["truncated"].Should().Be(true);
    }

    [Fact]
    public async Task OutputUnderHeadTail_ShouldShowFullContent()
    {
        // 配置小阈值（100 字节）使 150 字符触发，但 150 ≤ 头尾和（2048）→ 全展示无省略
        var content = new string('u', 150);
        var (_, decorator, _) = Create(new OutputTool { Result = content }, o => o.MaxInlineBytes = 100);
        var result = await ExecAsync(decorator);

        result.Output.Should().Contain("<persisted-output>");
        result.Output.Should().Contain("完整内容（未省略）");
        result.Output.Should().Contain(content);
    }

    [Fact]
    public async Task DisabledConfig_ShouldPassThrough()
    {
        var big = new string('x', 60 * 1024);
        var (_, decorator, _) = Create(new OutputTool { Result = big }, o => o.Enabled = false);
        var result = await ExecAsync(decorator);

        result.Output.Should().Be(big);
        result.Metadata.Should().NotContainKey("truncated");
    }

    [ToolCapability(ToolCapabilityKeys.OutputMaxBytes, "abc")]
    private sealed class InvalidMaxBytesTool : OutputTool
    {
    }

    [Fact]
    public async Task InvalidMaxBytesCapability_ShouldFallbackToGlobal()
    {
        var (_, decorator, _) = Create(new InvalidMaxBytesTool { Result = new string('x', 200) });
        var result = await ExecAsync(decorator);

        result.Output.Should().Be(new string('x', 200));
        result.Metadata.Should().NotContainKey("truncated");
    }
}
