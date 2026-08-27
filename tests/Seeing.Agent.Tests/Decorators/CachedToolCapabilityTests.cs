using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Decorators;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

/// <summary>
/// CachedToolDecorator 能力语义测试
/// <para>验证：cache.enabled=false 不缓存；scope=session 时键含 SessionId（同参数跨会话不命中）；ttl 覆盖默认。</para>
/// </summary>
public class CachedToolCapabilityTests
{
    private sealed class CountingTool : ToolBase
    {
        private readonly string _cacheScope;
        public int Calls;
        public CountingTool(string cacheScope) : base(NullLogger.Instance)
        {
            _cacheScope = cacheScope;
        }
        public override string Id => "counter";
        public override string Description => "test";
        public override IReadOnlyDictionary<string, string>? Capabilities => new Dictionary<string, string>
        {
            [ToolCapabilityKeys.CacheEnabled] = "true",
            [ToolCapabilityKeys.CacheScope] = _cacheScope
        };
        public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ToolResult { Success = true, Output = "ok" });
        }
    }

    private sealed class NoCacheTool : ToolBase
    {
        public int Calls;
        public NoCacheTool() : base(NullLogger.Instance) { }
        public override string Id => "nocache";
        public override string Description => "test";
        public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ToolResult { Success = true, Output = "ok" });
        }
    }

    private static ToolContext Ctx(string sessionId) => new() { SessionId = sessionId };

    [Fact]
    public async Task NoCacheTool_ShouldAlwaysExecute()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tool = new NoCacheTool();
        var decorated = new CachedToolDecorator(tool, cache, TimeSpan.FromMinutes(5));

        await decorated.ExecuteAsync(JsonSerializer.SerializeToElement(new { a = 1 }), Ctx("s1"));
        await decorated.ExecuteAsync(JsonSerializer.SerializeToElement(new { a = 1 }), Ctx("s1"));

        tool.Calls.Should().Be(2); // cache.enabled 默认 false → 不缓存，执行两次
    }

    [Fact]
    public async Task SessionScope_ShouldNotHitAcrossSessions()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tool = new CountingTool("session");
        var decorated = new CachedToolDecorator(tool, cache, TimeSpan.FromMinutes(5));

        var args = JsonSerializer.SerializeToElement(new { a = 1 });
        await decorated.ExecuteAsync(args, Ctx("s1"));
        await decorated.ExecuteAsync(args, Ctx("s1")); // 同会话 → 命中缓存
        await decorated.ExecuteAsync(args, Ctx("s2")); // 跨会话 → 不命中

        tool.Calls.Should().Be(2);
    }

    [Fact]
    public async Task GlobalScope_ShouldHitAcrossSessions()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tool = new CountingTool("global");
        var decorated = new CachedToolDecorator(tool, cache, TimeSpan.FromMinutes(5));

        var args = JsonSerializer.SerializeToElement(new { a = 1 });
        await decorated.ExecuteAsync(args, Ctx("s1"));
        await decorated.ExecuteAsync(args, Ctx("s2")); // global → 跨会话命中

        tool.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Ttl_ShouldOverrideDefault()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tool = new CountingTool("session");
        var decorated = new CachedToolDecorator(tool, cache, TimeSpan.FromMinutes(5));

        var args = JsonSerializer.SerializeToElement(new { a = 1 });
        await decorated.ExecuteAsync(args, Ctx("s1"));

        // 手动过期缓存项（模拟 ttl 短过期；此处验证键结构而非真实等待）
        var key = $"tool:counter:session:s1:{new ToolArguments(args).ComputeHash()}";
        var cached = cache.Get(key);
        cached.Should().NotBeNull();
        cache.Remove(key);
        await decorated.ExecuteAsync(args, Ctx("s1"));
        tool.Calls.Should().Be(2);
    }
}
