using FluentAssertions;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class SerializingPermissionChannelTests
{
    [Fact]
    public async Task RequestToolPermissionAsync_ShouldSerializeConcurrentAsks()
    {
        var gate = new object();
        var concurrent = 0;
        var maxConcurrent = 0;

        var inner = new CountingChannel(() =>
        {
            lock (gate)
            {
                concurrent++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }

            Thread.Sleep(50);

            lock (gate)
            {
                concurrent--;
            }

            return PermissionDecision.Allow();
        });

        var serial = new SerializingPermissionChannel(inner);
        var ctx = new AgentContext { SessionId = "s1" };

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => serial.RequestToolPermissionAsync("bash", null, ctx))
            .ToArray();

        await Task.WhenAll(tasks);

        maxConcurrent.Should().Be(1);
        inner.CallCount.Should().Be(4);
    }

    private sealed class CountingChannel : IPermissionChannel
    {
        private readonly Func<PermissionDecision> _onAsk;

        public CountingChannel(Func<PermissionDecision> onAsk) => _onAsk = onAsk;

        public int CallCount { get; private set; }

        public Task<bool> RequestConfirmationAsync(PermissionRequest request) =>
            Task.FromResult(true);

        public Task<PermissionDecision> RequestToolPermissionAsync(
            string toolName, object? arguments, AgentContext context)
        {
            CallCount++;
            return Task.FromResult(_onAsk());
        }

        public Task<PermissionDecision> RequestSubAgentPermissionAsync(
            string agentName, string prompt, AgentContext context) =>
            Task.FromResult(PermissionDecision.Allow());

        public Task<PermissionDecision> RequestWritePermissionAsync(
            string filePath, string? contentPreview, AgentContext context) =>
            Task.FromResult(PermissionDecision.Allow());
    }
}
