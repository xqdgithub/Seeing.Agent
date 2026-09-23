using System.Collections.Concurrent;
using FluentAssertions;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// <see cref="TuiViewState"/> 并发语义：Blocks/Tools 读取不可变快照，枚举期间并发 Upsert/Remove 不抛异常。
/// </summary>
public sealed class TuiViewStateConcurrencyTests
{
    [Fact]
    public async Task ConcurrentUpsertRemoveAndEnumeration_ShouldNotThrow()
    {
        var state = new TuiViewState { SessionId = "s" };
        var errors = new ConcurrentBag<Exception>();
        using var writerCts = new CancellationTokenSource();
        using var readerCts = new CancellationTokenSource();

        var writers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
        {
            var i = w;
            while (!writerCts.IsCancellationRequested)
            {
                try
                {
                    var key = $"k{i % 16}";
                    state.Upsert(new TuiBlock
                    {
                        Key = key,
                        Kind = TuiBlockKind.System,
                        Text = i.ToString(),
                    });

                    if ((i & 7) == 0)
                        state.Remove(key);

                    state.Touch();
                    i++;
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    writerCts.Cancel();
                }
            }
        })).ToArray();

        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
        {
            while (!readerCts.IsCancellationRequested)
            {
                try
                {
                    foreach (var block in state.Blocks)
                        _ = block.Text;

                    foreach (var block in state.Tools)
                        _ = block.Key;
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    readerCts.Cancel();
                }
            }
        })).ToArray();

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        await writerCts.CancelAsync();
        await readerCts.CancelAsync();
        await Task.WhenAll(writers.Concat(readers));

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Update_ShouldApplyMutationUnderLock()
    {
        var state = new TuiViewState { SessionId = "s" };
        state.Upsert(new TuiBlock
        {
            Key = "tool:1",
            Kind = TuiBlockKind.Tool,
            Tool = new TuiToolState { CallId = "1", Name = "bash" },
        });
        var before = state.Revision;

        var replacement = new TuiToolState { CallId = "1", Name = "bash" };
        replacement.Steps.Add(new TuiTaskStep("read", "f", TuiToolStatus.Success));

        state.Update("tool:1", b => b.Tool = replacement);

        state.Find("tool:1")!.Tool.Should().BeSameAs(replacement);
        state.Revision.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Update_UnknownKey_ShouldBeNoOp()
    {
        var state = new TuiViewState { SessionId = "s" };
        var before = state.Revision;

        var invoked = false;
        state.Update("missing", _ => invoked = true);

        invoked.Should().BeFalse();
        state.Revision.Should().Be(before);
    }

    [Fact]
    public void Upsert_ShouldPublishNewSnapshotCollection()
    {
        var state = new TuiViewState { SessionId = "s" };
        var snapshot0 = state.Blocks;

        state.Upsert(new TuiBlock { Key = "u", Kind = TuiBlockKind.User, Text = "hi" });

        var snapshot1 = state.Blocks;
        snapshot1.Should().NotBeSameAs(snapshot0);
        snapshot0.Should().BeEmpty();
        snapshot1.Should().ContainSingle();
        snapshot1[0].Text.Should().Be("hi");
    }

    [Fact]
    public void ResetFromSession_ShouldPublishNewSnapshotCollection()
    {
        var state = new TuiViewState { SessionId = "s" };
        state.Upsert(new TuiBlock { Key = "old", Kind = TuiBlockKind.System, Text = "x" });
        var snapshot0 = state.Blocks;

        var session = SessionData.Create();
        session.AddMessage(new SessionMessage { Role = MessageRole.User, Content = "hello" });
        state.ResetFromSession(session);

        var snapshot1 = state.Blocks;
        snapshot1.Should().NotBeSameAs(snapshot0);
        snapshot0.Should().ContainSingle();
        snapshot1.Should().ContainSingle();
    }
}
