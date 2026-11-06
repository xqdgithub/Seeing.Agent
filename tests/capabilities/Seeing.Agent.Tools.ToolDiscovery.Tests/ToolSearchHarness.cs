using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.ToolDiscovery.Tests;

/// <summary>测试脚手架：用给定语料构造 <see cref="ToolSearchTool"/> 与其执行作用域。</summary>
internal static class ToolSearchHarness
{
    /// <summary>构造工具与语料对应的执行作用域（ToolScope 经 ToolContext 传入）。</summary>
    public static (ToolSearchTool Tool, IExecutionToolScope Scope) Create(params ExposedTool[] corpus)
    {
        var resolver = new Mock<ISessionToolSchemaResolver>();
        var store = new Mock<IToolDiscoveryStore>();
        store.Setup(s => s.Add(It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(true);

        return (new ToolSearchTool(resolver.Object, store.Object), new Scope(corpus));
    }

    private sealed class Scope(params ExposedTool[] corpus) : IExecutionToolScope
    {
        public IReadOnlyList<ExposedTool> DeferredTools { get; } = corpus;
        public IReadOnlyList<string> LoadedIds => Array.Empty<string>();
        public bool IsCallable(string toolId) => false;
        public bool IsDeferred(string toolId) => true;
        public int TryLoad(IEnumerable<string> toolIds) => 0;
    }
}
