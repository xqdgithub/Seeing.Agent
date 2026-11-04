using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// A-1 守门：<see cref="LlmService"/> 收敛为纯传输层——不再承载任何 prompt 改写能力，
/// 同时保留的参数/请求头/观测钩子行为不变。
/// </summary>
public class LlmTransportOnlyTests
{
    [Fact]
    public void HookRegistry_Should_Not_Define_Prompt_Rewriting_Hooks()
    {
        var names = typeof(HookRegistry)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.Name).ToList();

        names.Should().NotContain("ChatBeforeStart");
        names.Should().NotContain("LlmSystemPrompt");
    }

    [Fact]
    public async Task CompleteAsync_Should_Not_Trigger_Removed_Hooks()
    {
        var (llm, hooks) = BuildLlm();

        await llm.CompleteAsync("m1", MakeRequest(), "ses_1", TestContext.Current.CancellationToken);

        hooks.TriggeredPoints.Should().NotContain("chat.before_start");
        hooks.TriggeredPoints.Should().NotContain("llm.system_prompt");
    }

    [Fact]
    public async Task CompleteAsync_Should_Still_Trigger_Transport_Hooks()
    {
        // Review Focus #2：保留的传输层钩子不变
        var (llm, hooks) = BuildLlm();

        await llm.CompleteAsync("m1", MakeRequest(), "ses_1", TestContext.Current.CancellationToken);

        hooks.TriggeredPoints.Should().Contain("llm.params");
        hooks.TriggeredPoints.Should().Contain("llm.headers");
        hooks.TriggeredPoints.Should().Contain("llm.after_complete");
    }

    [Fact]
    public async Task CompleteAsync_Should_Not_Modify_SystemPrompt()
    {
        // 回归守门：改前就通过（旧 LlmSystemPrompt 零订阅），改后必须继续通过
        var (llm, _) = BuildLlm();
        var request = MakeRequest(systemPrompt: "原始提示");

        await llm.CompleteAsync("m1", request, "ses_1", TestContext.Current.CancellationToken);

        request.SystemPrompt.Should().Be("原始提示");
    }

    [Fact]
    public async Task CompleteStreamAsync_Should_Not_Modify_SystemPrompt()
    {
        var (llm, _) = BuildLlm();
        var request = MakeRequest(systemPrompt: "原始提示");

        await foreach (var _ in llm.CompleteStreamAsync(
            "m1", request, "ses_1", TestContext.Current.CancellationToken))
        {
        }

        request.SystemPrompt.Should().Be("原始提示");
    }

    private static ChatRequest MakeRequest(string? systemPrompt = null) => new()
    {
        SystemPrompt = systemPrompt,
        Messages = [new ChatMessage { Role = ChatRole.User, Content = "你好" }]
    };

    private static (LlmService Llm, RecordingHookManager Hooks) BuildLlm()
    {
        var modelConfig = new ModelConfig
        {
            Id = "m1",
            Provider = "p1",
            Types = [ModelType.Text]
        };

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.GetModel("m1")).Returns(modelConfig);

        var client = new Mock<ILlmClient>();
        client.SetupGet(c => c.ProviderId).Returns("p1");
        client.SetupGet(c => c.ProviderType).Returns("openai");
        client.Setup(c => c.CompleteAsync(
                It.IsAny<ChatRequest>(), It.IsAny<LlmCallContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ChatResponse
            {
                Id = "resp_1",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "回复" }
            });
        client.Setup(c => c.CompleteStreamAsync(
                It.IsAny<ChatRequest>(), It.IsAny<LlmCallContext?>(), It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var providerManager = new Mock<IProviderManager>();
        providerManager.Setup(p => p.GetClient("p1")).Returns(client.Object);

        var workspace = new Mock<IWorkspaceProvider>();
        var configManager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());

        var hooks = new RecordingHookManager();
        var llm = new LlmService(
            providerManager.Object,
            modelManager.Object,
            configManager,
            hooks,
            NullLogger<LlmService>.Instance);

        return (llm, hooks);
    }

    private static async IAsyncEnumerable<StreamUpdate> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>最小 IHookManager 替身：只记录被触发的 Hook 点名，不执行任何处理器。</summary>
    private sealed class RecordingHookManager : IHookManager
    {
        private readonly List<string> _points = [];

        /// <summary>按触发顺序记录的全部 Hook 点名。</summary>
        public IReadOnlyList<string> TriggeredPoints => _points;

        /// <inheritdoc />
        public long Version => 0;

        /// <inheritdoc />
        public void Register(IHookHandler handler) => _points.Add(handler.Spec.Point);

        /// <inheritdoc />
        public void RegisterMulti(IMultiHookHandler handler)
        {
        }

        /// <inheritdoc />
        public bool Remove(IHookHandler handler) => false;

        /// <inheritdoc />
        public bool Remove(IMultiHookHandler handler) => false;

        /// <inheritdoc />
        public bool Clear(HookSpec spec) => false;

        /// <inheritdoc />
        public int Count(HookSpec spec) => 0;

        /// <inheritdoc />
        public Task<HookResult> TriggerAsync(HookPayload payload)
        {
            _points.Add(payload.Spec.Point);
            return Task.FromResult(HookResult.Success);
        }

        /// <inheritdoc />
        public Task<HookResult> TriggerBlockingAsync(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            IDictionary<string, object?>? mutable = null,
            CancellationToken cancellationToken = default)
        {
            _points.Add(spec.Point);
            return Task.FromResult(HookResult.Success);
        }

        /// <inheritdoc />
        public void TriggerFireAndForget(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            IReadOnlyDictionary<string, object?>? result = null)
            => _points.Add(spec.Point);

        /// <inheritdoc />
        public Task TriggerParallelAsync(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            CancellationToken cancellationToken = default)
        {
            _points.Add(spec.Point);
            return Task.CompletedTask;
        }
    }
}
