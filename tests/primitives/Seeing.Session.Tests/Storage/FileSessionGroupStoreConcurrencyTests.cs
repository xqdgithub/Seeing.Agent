using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

/// <summary>
/// 多实例并发写同一目录的回归测试。
/// <para>
/// 历史缺陷：会话组存储的临时文件名固定为 <c>{file}.tmp</c>、锁是实例级的。
/// 测试夹具与宿主会同时创建「冷/热」两个 <see cref="FileSessionGroupStore"/> 指向同一目录，
/// 两个实例并发保存同一组时会互相移动对方的临时文件，抛
/// <see cref="UnauthorizedAccessException"/>（Windows 上表现为间歇性失败）。
/// </para>
/// <para>
/// 修复后：锁按目标路径在进程级共享，临时文件名唯一，移动带重试。
/// </para>
/// </summary>
public class FileSessionGroupStoreConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "seeing-group-concurrency-" + Guid.NewGuid().ToString("N"));

    public FileSessionGroupStoreConcurrencyTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task TwoInstances_SavingSameGroupConcurrently_ShouldNotThrow()
    {
        // 两个独立实例指向同一目录：实例级锁无法互斥，必须依赖进程级按路径锁与唯一临时名
        var storeA = new FileSessionGroupStore(_dir);
        var storeB = new FileSessionGroupStore(_dir);

        var group = new SessionGroup
        {
            Id = "grp_concurrent",
            AnchorSessionId = "anchor-session",
            CreatedAt = DateTime.Now
        };

        var tasks = Enumerable.Range(0, 16).Select(i =>
        {
            var store = i % 2 == 0 ? storeA : storeB;
            return store.SaveAsync(group, TestContext.Current.CancellationToken);
        });

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        // 落盘结果必须是完整可解析的 JSON（不是半写文件）
        var loaded = await storeA.LoadAsync(group.Id, TestContext.Current.CancellationToken);
        loaded.Should().NotBeNull();
        loaded!.AnchorSessionId.Should().Be(group.AnchorSessionId);

        // 不残留临时文件
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task TwoInstances_SavingDifferentGroupsConcurrently_ShouldNotThrow()
    {
        var storeA = new FileSessionGroupStore(_dir);
        var storeB = new FileSessionGroupStore(_dir);

        var tasks = Enumerable.Range(0, 8).Select(i =>
        {
            var store = i % 2 == 0 ? storeA : storeB;
            var group = new SessionGroup
            {
                Id = $"grp_{i}",
                AnchorSessionId = $"session-{i}",
                CreatedAt = DateTime.Now
            };
            return store.SaveAsync(group, TestContext.Current.CancellationToken);
        });

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        Directory.GetFiles(_dir, "*.json").Should().HaveCount(8);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }
}
