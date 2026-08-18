using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Permission;
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Models;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class SerializingPermissionChannelTests
{
    [Fact]
    public async Task RequestAsync_ShouldSerializeConcurrentAsks()
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

            return PermissionChannelResult.Allowed();
        });

        var serial = new SerializingPermissionChannel(inner, new NoOpPermissionMemory());
        var request = new PermissionRequest
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            SessionId = "s1"
        };

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => serial.RequestAsync(request))
            .ToArray();

        await Task.WhenAll(tasks);

        maxConcurrent.Should().Be(1);
        inner.CallCount.Should().Be(4);
    }

    [Fact]
    public async Task RequestAsync_WhitelistedPathOutsideWorkspace_ShouldAllowWithoutInnerCall()
    {
        var inner = new CountingChannel(() => PermissionChannelResult.Allowed());
        var whitelist = new SessionWorkspaceWhitelist();
        whitelist.Add("s1", @"C:\data");

        var serial = new SerializingPermissionChannel(inner, new NoOpPermissionMemory(), whitelist: whitelist);
        var request = new PermissionRequest
        {
            PermissionKind = "filesystem.read",
            Resource = @"C:\data\file.txt",
            SessionId = "s1"
        };

        var result = await serial.RequestAsync(request);

        result.Action.Should().Be(PermissionChannelAction.Allow);
        inner.CallCount.Should().Be(0);
    }

    private sealed class CountingChannel : IPermissionChannel
    {
        private readonly Func<PermissionChannelResult> _onRequest;

        public CountingChannel(Func<PermissionChannelResult> onRequest) => _onRequest = onRequest;

        public int CallCount { get; private set; }

        public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_onRequest());
        }
    }

    private sealed class NoOpPermissionMemory : IPermissionMemory
    {
        public PermissionMemoryEntry? Match(string permissionKind, string? resource, string sessionId)
            => null;

        public void Remember(string sessionId, PermissionMemoryEntry entry) { }

        public void Forget(string sessionId, string? resource) { }

        public void ClearSession(string sessionId) { }
    }
}
