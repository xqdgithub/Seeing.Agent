using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Tests.Hooks
{
    public class HookManagerTests
    {
        private readonly Mock<ILogger<HookManager>> _loggerMock;
        private readonly HookManager _manager;

        public HookManagerTests()
        {
            _loggerMock = new Mock<ILogger<HookManager>>();
            _manager = new HookManager(_loggerMock.Object);
        }

        [Fact]
        public void Register_ShouldAddHandler()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler.Setup(h => h.Priority).Returns(10);

            _manager.Register(handler.Object);

            _manager.Count(HookRegistry.ToolExecuteBefore).Should().Be(1);
        }

        [Fact]
        public void Register_ShouldOrderByPriority()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.Spec).Returns(HookRegistry.LlmParams);
            handler1.Setup(h => h.Priority).Returns(20);

            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.Spec).Returns(HookRegistry.LlmParams);
            handler2.Setup(h => h.Priority).Returns(10);

            _manager.Register(handler1.Object);
            _manager.Register(handler2.Object);

            _manager.Count(HookRegistry.LlmParams).Should().Be(2);
        }

        // ===== H-1：chat.* → llm.* 的废弃点兜底 =====

        [Fact]
        public void Register_LegacyChatPoint_ShouldLogWarningAndSkipRegistration()
        {
            var legacy = new HookSpec(HookPolicy.FireAndForget, "chat.after_complete");
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(legacy);

            var before = _manager.Version;
            _manager.Register(handler.Object);

            // 未登记到旧点，也不会"顺手"落到新点上：它永远不可能被触发
            _manager.Count(legacy).Should().Be(0);
            _manager.Count(HookRegistry.LlmAfterComplete).Should().Be(0);
            _manager.Version.Should().Be(before, "登记未生效，不该产生缓存失效信号");
            VerifyWarningContaining("已改名");
        }

        [Theory]
        [InlineData("chat.params", HookPolicy.Blocking)]
        [InlineData("chat.headers", HookPolicy.Blocking)]
        [InlineData("chat.message", HookPolicy.Parallel)]
        [InlineData("chat.after_complete", HookPolicy.FireAndForget)]
        [InlineData("chat.on_error", HookPolicy.FireAndForget)]
        public void Register_EveryLegacyChatPoint_ShouldWarn(string point, HookPolicy policy)
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(new HookSpec(policy, point));

            _manager.Register(handler.Object);

            _manager.Count(new HookSpec(policy, point)).Should().Be(0);
            VerifyWarningContaining("已改名");
        }

        [Fact]
        public async Task Register_LegacyChatPoint_ShouldNeverFire_WhenLlmPointTriggered()
        {
            // 用 Blocking 的新点做无竞态断言：若旧注册被"改名"挂到了新点，这里必然被调用到
            var fired = false;
            var legacy = new HookSpec(HookPolicy.Blocking, "chat.params");
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(legacy);
            handler.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
                .Callback(() => fired = true)
                .ReturnsAsync(HookResult.Success);

            _manager.Register(handler.Object);
            await _manager.TriggerBlockingAsync(
                HookRegistry.LlmParams, "session-1", cancellationToken: TestContext.Current.CancellationToken);

            fired.Should().BeFalse();
            handler.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Never);
        }

        [Fact]
        public void RegisterMulti_AllLegacyPoints_ShouldRegisterNothing()
        {
            var handler = new Mock<IMultiHookHandler>();
            handler.Setup(h => h.Specs).Returns(new[]
            {
                new HookSpec(HookPolicy.Blocking, "chat.params"),
                new HookSpec(HookPolicy.Blocking, "chat.headers"),
                new HookSpec(HookPolicy.Parallel, "chat.message"),
                new HookSpec(HookPolicy.FireAndForget, "chat.after_complete"),
                new HookSpec(HookPolicy.FireAndForget, "chat.on_error"),
            });

            var before = _manager.Version;
            _manager.RegisterMulti(handler.Object);

            _manager.Version.Should().Be(before);
            _manager.Count(HookRegistry.LlmParams).Should().Be(0);
            _manager.Count(HookRegistry.LlmAfterComplete).Should().Be(0);
            VerifyWarningContaining("已改名", Times.Exactly(5));
        }

        [Fact]
        public void Register_RenamedLlmPoint_ShouldStillWork()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(HookRegistry.LlmAfterComplete);

            _manager.Register(handler.Object);

            _manager.Count(HookRegistry.LlmAfterComplete).Should().Be(1);
        }

        [Fact]
        public void HookRegistry_LlmPoints_ShouldHaveSpecNamesAndPolicies()
        {
            HookRegistry.LlmParams.Point.Should().Be("llm.params");
            HookRegistry.LlmHeaders.Point.Should().Be("llm.headers");
            HookRegistry.LlmMessage.Point.Should().Be("llm.message");
            HookRegistry.LlmAfterComplete.Point.Should().Be("llm.after_complete");
            HookRegistry.LlmOnError.Point.Should().Be("llm.on_error");

            HookRegistry.LlmParams.Policy.Should().Be(HookPolicy.Blocking);
            HookRegistry.LlmHeaders.Policy.Should().Be(HookPolicy.Blocking);
            HookRegistry.LlmMessage.Policy.Should().Be(HookPolicy.Parallel);
            HookRegistry.LlmAfterComplete.Policy.Should().Be(HookPolicy.FireAndForget);
            HookRegistry.LlmOnError.Policy.Should().Be(HookPolicy.FireAndForget);
        }

        [Fact]
        public void HookRegistry_ShouldHaveNoChatPointLeft()
        {
            // 彻底重命名、不留别名（spec §7.6）：注册表里再不该出现 chat.* 点或 Chat* 常量
            var fields = typeof(HookRegistry)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => f.Name)
                .ToList();

            fields.Should().NotContain("ChatParams")
                .And.NotContain("ChatHeaders")
                .And.NotContain("ChatMessage")
                .And.NotContain("ChatAfterComplete")
                .And.NotContain("ChatOnError");

            typeof(HookRegistry)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => ((HookSpec)f.GetValue(null)!).Point)
                .Should().NotContain(p => p.StartsWith("chat.", StringComparison.Ordinal));
        }

        [Fact]
        public void LlmAfterComplete_Contract_ShouldCoverEverySpecKey()
        {
            // spec §7.6 的 result 超集键必须全部有常量，插件不许手拼字符串
            HookDataContract.LlmAfterComplete.Capability.Name.Should().Be("capability");
            HookDataContract.LlmAfterComplete.Provider.Name.Should().Be("provider");
            HookDataContract.LlmAfterComplete.Model.Name.Should().Be("model");
            HookDataContract.LlmAfterComplete.Usage.Name.Should().Be("usage");
            HookDataContract.LlmAfterComplete.Duration.Name.Should().Be("duration");
            HookDataContract.LlmAfterComplete.Content.Name.Should().Be("content");
            HookDataContract.LlmAfterComplete.Reasoning.Name.Should().Be("reasoning");
            HookDataContract.LlmAfterComplete.ToolCalls.Name.Should().Be("toolCalls");
            HookDataContract.LlmAfterComplete.MessageId.Name.Should().Be("messageId");
            HookDataContract.LlmAfterComplete.Streaming.Name.Should().Be("streaming");
            HookDataContract.LlmAfterComplete.Step.Name.Should().Be("step");
            HookDataContract.LlmAfterComplete.VectorCount.Name.Should().Be("vectorCount");
            HookDataContract.LlmAfterComplete.Dimensions.Name.Should().Be("dimensions");
            HookDataContract.LlmAfterComplete.Answers.Name.Should().Be("answers");
            HookDataContract.LlmAfterComplete.AudioBytes.Name.Should().Be("audioBytes");

            HookDataContract.LlmAfterComplete.Duration.GetFrom(
                new Dictionary<string, object?> { ["duration"] = TimeSpan.FromMilliseconds(120) })
                .Should().Be(TimeSpan.FromMilliseconds(120));
        }

        [Fact]
        public async Task TriggerAsync_ShouldCallAllHandlers()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler1.Setup(h => h.Priority).Returns(10);
            handler1.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
                .ReturnsAsync(HookResult.Success);

            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler2.Setup(h => h.Priority).Returns(20);
            handler2.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
                .ReturnsAsync(HookResult.Success);

            _manager.Register(handler1.Object);
            _manager.Register(handler2.Object);

            var payload = HookPayload.Blocking(HookRegistry.ToolExecuteBefore, "test-session", cancellationToken: TestContext.Current.CancellationToken);
            var result = await _manager.TriggerAsync(payload);

            result.Continue.Should().BeTrue();
            handler1.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Once);
            handler2.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Once);
        }

        [Fact]
        public async Task TriggerAsync_ShouldStopWhenContinueIsFalse()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler1.Setup(h => h.Priority).Returns(10);
            handler1.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
                .ReturnsAsync(HookResult.Stop);

            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler2.Setup(h => h.Priority).Returns(20);

            _manager.Register(handler1.Object);
            _manager.Register(handler2.Object);

            var payload = HookPayload.Blocking(HookRegistry.ToolExecuteBefore, "test-session", cancellationToken: TestContext.Current.CancellationToken);
            var result = await _manager.TriggerAsync(payload);

            result.Continue.Should().BeFalse();
            handler1.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Once);
            handler2.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Never);
        }

        [Fact]
        public async Task TriggerAsync_ShouldReturnContinueTrue_WhenNoHandlers()
        {
            var payload = HookPayload.Blocking(
                new HookSpec(HookPolicy.Blocking, "nonexistent_hook"),
                "test-session", cancellationToken: TestContext.Current.CancellationToken);

            var result = await _manager.TriggerAsync(payload);

            result.Continue.Should().BeTrue();
        }

        [Fact]
        public void Register_ShouldIgnoreNullHandler()
        {
            _manager.Register(null!);

            _manager.Count(HookRegistry.ToolExecuteBefore).Should().Be(0);
        }

        [Fact]
        public void Version_ShouldIncreaseOnRegister()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);

            var before = _manager.Version;
            _manager.Register(handler.Object);

            _manager.Version.Should().BeGreaterThan(before);
        }

        [Fact]
        public void Version_ShouldIncreaseOnRegisterMulti()
        {
            var handler = new Mock<IMultiHookHandler>();
            handler.Setup(h => h.Specs).Returns(new[] { HookRegistry.ToolDefinition, HookRegistry.ToolBeforeRegister });

            var before = _manager.Version;
            _manager.RegisterMulti(handler.Object);

            _manager.Version.Should().BeGreaterThan(before);
        }

        [Fact]
        public void Version_ShouldIncreaseOnRemove()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            _manager.Register(handler.Object);

            var before = _manager.Version;
            _manager.Remove(handler.Object).Should().BeTrue();

            _manager.Version.Should().BeGreaterThan(before);
        }

        [Fact]
        public void Version_ShouldNotIncreaseOnFailedRemove()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);

            var before = _manager.Version;
            _manager.Remove(handler.Object).Should().BeFalse();

            _manager.Version.Should().Be(before);
        }

        [Fact]
        public void Version_ShouldIncreaseOnClear()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(HookRegistry.ToolDefinition);
            _manager.Register(handler.Object);

            var before = _manager.Version;
            _manager.Clear(HookRegistry.ToolDefinition).Should().BeTrue();

            _manager.Version.Should().BeGreaterThan(before);
        }

        [Fact]
        public void Version_ShouldBeMonotonicallyIncreasing()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(HookRegistry.ToolDefinition);

            var v0 = _manager.Version;
            _manager.Register(handler.Object);
            var v1 = _manager.Version;
            _manager.Remove(handler.Object);
            var v2 = _manager.Version;
            _manager.Clear(HookRegistry.ToolDefinition);
            var v3 = _manager.Version;

            v1.Should().BeGreaterThan(v0);
            v2.Should().BeGreaterThan(v1);
            v3.Should().BeGreaterThan(v2);
        }

        /// <summary>断言记过至少一条文案含 <paramref name="fragment"/> 的 Warning 日志。</summary>
        private void VerifyWarningContaining(string fragment, Times? expected = null) =>
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(fragment)),
                    It.IsAny<Exception?>(),
                    (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                expected ?? Times.Once());
    }
}
