using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// 共享 <see cref="Seeing.Agent.Tools.FileSystem.RipgrepSearch"/> 进程级探测缓存的测试集合。
/// <para>
/// 该缓存是静态状态，多个测试类并行执行会互相污染（一个类重置/填充缓存会改变另一类的探测结果）。
/// 归入同一 collection 后 xUnit 串行执行这些类。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RipgrepStaticStateCollection
{
    /// <summary>集合名称。</summary>
    public const string Name = "RipgrepStaticState";
}
