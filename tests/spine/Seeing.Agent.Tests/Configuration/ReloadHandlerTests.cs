using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadHandlerTests
{
    private sealed class TestHandler : ReloadHandlerBase<ConfigChange>
    {
        public override string ComponentId => "test";
        public List<string> Received = new();
        protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
        {
            Received.Add(string.Join(",", change.ChangedSections));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ChangeTypes_声明订阅类型()
    {
        var handler = new TestHandler();
        handler.ChangeTypes.Should().Contain(typeof(ConfigChange));
    }

    [Fact]
    public async Task ReloadAsync_匹配类型分发()
    {
        var handler = new TestHandler();
        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "X" } }, CancellationToken.None);
        handler.Received.Should().Contain("X");
    }

    [Fact]
    public async Task ReloadAsync_不匹配类型忽略()
    {
        var handler = new TestHandler();
        await handler.ReloadAsync(new WorkspaceChange { NewWorkspace = "/new" }, CancellationToken.None);
        handler.Received.Should().BeEmpty();
    }
}
