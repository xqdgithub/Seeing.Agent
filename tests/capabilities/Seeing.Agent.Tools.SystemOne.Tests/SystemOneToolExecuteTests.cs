using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneToolExecuteTests
{
    private sealed class FakeSystemOneService : ISystemOneService
    {
        public SystemOneRequest? LastRequest { get; private set; }
        public SystemOneResponse Response { get; set; } = new();
        public Exception? Throw { get; set; }

        public Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Throw is null ? Task.FromResult(Response) : Task.FromException<SystemOneResponse>(Throw);
        }

        public Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(string? providerId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SystemOneModel>>(Array.Empty<SystemOneModel>());
    }

    private static (SystemOneTool Tool, ToolContext Context, FakeSystemOneService Service) Create(
        SystemOneToolKind kind, FakeSystemOneService? service = null)
    {
        service ??= new FakeSystemOneService();
        var provider = new ServiceCollection()
            .AddSingleton<ISystemOneService>(service)
            .BuildServiceProvider();

        var context = new ToolContext { Services = provider, CancellationToken = CancellationToken.None };
        return (new SystemOneTool(kind, NullLogger<SystemOneTool>.Instance), context, service);
    }

    [Fact]
    public async Task ask_执行_映射并返回定界JSON()
    {
        var (tool, context, service) = Create(SystemOneToolKind.Ask);
        var args = JsonDocument.Parse("""
        {"state":{"text":"text"},"questions":[
          {"id":"n","type":"noul","instructions":"q1","criteriaTrue":"y","criteriaFalse":"n"},
          {"id":"c","type":"choice","instructions":"q2","options":[{"label":"a","description":"da"},{"label":"b"}]},
          {"id":"s","type":"score","instructions":"q3","levels":["lo","hi"]}
        ]}
        """).RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("<<<SYSTEMONE_ANSWERS_BEGIN>>>").And.Contain("<<<SYSTEMONE_ANSWERS_END>>>");
        service.LastRequest!.Questions.Should().HaveCount(3);
        service.LastRequest.Questions["c"].Criteria.Should().BeOfType<Dictionary<string, object?>>();
        service.LastRequest.Questions["s"].Criteria.Should().BeOfType<string[]>();
        service.LastRequest.Questions["n"].Criteria.Should().BeOfType<Dictionary<string, object?>>();
    }

    [Fact]
    public async Task 同类noul_缺id_请求键应为自动id()
    {
        var (tool, context, service) = Create(SystemOneToolKind.Noul);
        var args = JsonDocument.Parse("""{"state":{"text":"s"},"questions":[{"instructions":"x"}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeTrue(result.Error);
        service.LastRequest!.Questions.Keys.Should().Equal("q0");
        service.LastRequest.Questions["q0"].Type.Should().Be("noul");
    }

    [Fact]
    public async Task 同类noul_无criteria_请求Criteria应为null()
    {
        var (tool, context, service) = Create(SystemOneToolKind.Noul);
        var args = JsonDocument.Parse("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x"}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeTrue(result.Error);
        service.LastRequest!.Questions["a"].Criteria.Should().BeNull();
    }

    [Fact]
    public async Task state为对象_应作为结构化值透传()
    {
        var (tool, context, service) = Create(SystemOneToolKind.Noul);
        var args = JsonDocument.Parse("""{"state":{"ticket":"x"},"questions":[{"instructions":"x"}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeTrue(result.Error);
        service.LastRequest!.State.Should().BeOfType<JsonElement>();
    }

    [Fact]
    public async Task 未接入SystemOne_应失败()
    {
        var tool = new SystemOneTool(SystemOneToolKind.Score, NullLogger<SystemOneTool>.Instance);
        var context = new ToolContext { Services = new ServiceCollection().BuildServiceProvider() };
        var args = JsonDocument.Parse("""{"state":{"text":"s"},"questions":[{"instructions":"x","levels":["a","b"]}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("SystemOne");
    }

    [Fact]
    public async Task SystemOneException_应失败带状态码()
    {
        var service = new FakeSystemOneService { Throw = new SystemOneException(429, "rate", "限流") };
        var (tool, context, _) = Create(SystemOneToolKind.Noul, service);
        var args = JsonDocument.Parse("""{"state":{"text":"s"},"questions":[{"instructions":"x"}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("429");
    }

    [Fact]
    public async Task InvalidOperationException_应失败()
    {
        var service = new FakeSystemOneService { Throw = new InvalidOperationException("未配置可用的 SystemOne provider") };
        var (tool, context, _) = Create(SystemOneToolKind.Noul, service);
        var args = JsonDocument.Parse("""{"state":{"text":"s"},"questions":[{"instructions":"x"}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("provider");
    }

    [Fact]
    public async Task 取消_应向外抛()
    {
        var service = new FakeSystemOneService { Throw = new OperationCanceledException() };
        var (tool, context, _) = Create(SystemOneToolKind.Noul, service);
        var args = JsonDocument.Parse("""{"state":{"text":"s"},"questions":[{"instructions":"x"}]}""").RootElement;

        var act = async () => await tool.ExecuteAsync(args, context);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
