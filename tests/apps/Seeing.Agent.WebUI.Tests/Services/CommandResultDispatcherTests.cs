using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class CommandResultDispatcherTests
{
    private static (CommandResultDispatcher Dispatcher, MessageTimelineStore Store, HostActionRegistry Registry) Create()
    {
        var store = new MessageTimelineStore();
        var registry = new HostActionRegistry();
        return (new CommandResultDispatcher(registry, store, NullLogger<CommandResultDispatcher>.Instance), store, registry);
    }

    private static CommandResultEvent Evt(
        string? message,
        CommandAction? action = null,
        bool shouldContinue = false,
        DateTime? timestamp = null)
        => new()
        {
            SessionId = "s1",
            CommandName = "cron-list",
            Success = true,
            Message = message,
            Action = action,
            ShouldContinue = shouldContinue,
            Timestamp = timestamp ?? DateTime.UtcNow
        };

    [Fact]
    public async Task Event_Produced_Before_Binding_Should_Be_Ignored()
    {
        var (dispatcher, store, registry) = Create();
        using var registration = registry.Register("s1", "model.set", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Handled, "已切换")));

        var boundAt = DateTime.UtcNow;
        var replayed = Evt(
            "旧事件",
            new CommandAction { Name = "model.set" },
            timestamp: boundAt.AddMinutes(-5));

        await dispatcher.DispatchAsync("s1", replayed, boundAt);

        store.Items.Should().BeEmpty("回放的历史命令不得重新执行或重新提示");
    }

    [Fact]
    public async Task Continue_Command_Without_Action_Should_Not_Append_Message()
    {
        var (dispatcher, store, _) = Create();
        await dispatcher.DispatchAsync("s1", Evt("整段展开后的模板", shouldContinue: true));
        store.Items.Should().BeEmpty("继续执行类命令的 Message 是传输字段，展开内容已在会话里");
    }

    [Fact]
    public async Task No_Action_With_Message_Should_Append_Notice()
    {
        var (dispatcher, store, _) = Create();
        await dispatcher.DispatchAsync("s1", Evt("job-1"));
        store.Items.Should().ContainSingle(i => i.Message!.Content == "job-1");
    }

    [Fact]
    public async Task Handled_Action_With_Message_Should_Append_Action_Message_Not_Raw_Message()
    {
        var (dispatcher, store, registry) = Create();
        using var registration = registry.Register("s1", "model.picker", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Handled, "已打开模型选择器")));

        await dispatcher.DispatchAsync("s1", Evt("很长的模型列表", new CommandAction { Name = "model.picker" }));

        store.Items.Should().ContainSingle(i => i.Message!.Content == "已打开模型选择器");
        store.Items.Should().NotContain(i => i.Message!.Content == "很长的模型列表");
    }

    [Fact]
    public async Task Unavailable_Action_Should_Show_Reason()
    {
        var (dispatcher, store, registry) = Create();
        using var registration = registry.Register("s1", "model.set", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Unavailable, "ACP 会话不可切换模型")));

        await dispatcher.DispatchAsync("s1", Evt("回退文本", new CommandAction { Name = "model.set" }));

        store.Items.Should().ContainSingle(i => i.Message!.Content == "ACP 会话不可切换模型");
        store.Items.Should().NotContain(i => i.Message!.Content == "回退文本");
    }

    [Fact]
    public async Task NotHandled_Action_Should_Fall_Back_To_Raw_Message()
    {
        var (dispatcher, store, _) = Create();
        await dispatcher.DispatchAsync("s1", Evt("回退文本", new CommandAction { Name = "unknown" }));
        store.Items.Should().ContainSingle(i => i.Message!.Content == "回退文本");
    }
}
