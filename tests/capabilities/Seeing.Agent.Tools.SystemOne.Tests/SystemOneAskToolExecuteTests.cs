using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneAskToolExecuteTests
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

    private static (SystemOneAskTool Tool, ToolContext Context, FakeSystemOneService Service) Create(FakeSystemOneService? service = null)
    {
        service ??= new FakeSystemOneService();
        var provider = new ServiceCollection()
            .AddSingleton<ISystemOneService>(service)
            .BuildServiceProvider();

        var context = new ToolContext { Services = provider, CancellationToken = CancellationToken.None };
        return (new SystemOneAskTool(NullLogger<SystemOneAskTool>.Instance), context, service);
    }

    [Fact]
    public async Task 执行_应映射请求并返回定界JSON()
    {
        var (tool, context, service) = Create();
        var args = JsonDocument.Parse("""
        {
          "state": "text",
          "questions": [
            { "id": "n", "type": "noul", "instructions": "q1", "criteriaTrue": "y", "criteriaFalse": "n" },
            { "id": "c", "type": "choice", "instructions": "q2", "options": [ { "label": "a", "description": "da" }, { "label": "b" } ] },
            { "id": "s", "type": "score", "instructions": "q3", "levels": [ "lo", "hi" ] }
          ]
        }
        """).RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("<<<SYSTEMONE_ANSWERS_BEGIN>>>").And.Contain("<<<SYSTEMONE_ANSWERS_END>>>");
        result.Output.Should().Contain("answers");
        result.Metadata.Should().ContainKey("answer_count");

        service.LastRequest.Should().NotBeNull();
        service.LastRequest!.State.Should().Be("text");
        service.LastRequest.Questions.Should().HaveCount(3);
        service.LastRequest.Questions["n"].Type.Should().Be("noul");
        service.LastRequest.Questions["c"].Criteria.Should().BeOfType<Dictionary<string, object?>>();
        service.LastRequest.Questions["s"].Criteria.Should().BeOfType<string[]>();
    }

    [Fact]
    public async Task 执行_请求级model_应透传()
    {
        var (tool, context, service) = Create();
        var args = JsonDocument.Parse("""{"state":"s","model":"jev-1.13.0","questions":[{"id":"a","type":"noul","instructions":"x"}]}""").RootElement;

        await tool.ExecuteAsync(args, context);

        service.LastRequest!.Model.Should().Be("jev-1.13.0");
    }

    [Fact]
    public async Task 执行_未提供model_应传空串()
    {
        var (tool, context, service) = Create();
        var args = JsonDocument.Parse("""{"state":"s","questions":[{"id":"a","type":"noul","instructions":"x"}]}""").RootElement;

        await tool.ExecuteAsync(args, context);

        service.LastRequest!.Model.Should().BeEmpty();
    }

    [Fact]
    public async Task 执行_无效参数_应失败()
    {
        var (tool, context, _) = Create();
        var args = JsonDocument.Parse("""{"questions":[]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task 执行_未接入SystemOne_应失败()
    {
        var tool = new SystemOneAskTool(NullLogger<SystemOneAskTool>.Instance);
        var context = new ToolContext { Services = new ServiceCollection().BuildServiceProvider() };
        var args = JsonDocument.Parse("""{"state":"s","questions":[{"id":"a","type":"noul","instructions":"x"}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("SystemOne");
    }

    [Fact]
    public async Task 执行_SystemOneException_应失败带状态码()
    {
        var service = new FakeSystemOneService { Throw = new SystemOneException(429, "rate", "限流") };
        var (tool, context, _) = Create(service);
        var args = JsonDocument.Parse("""{"state":"s","questions":[{"id":"a","type":"noul","instructions":"x"}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("429");
    }

    [Fact]
    public async Task 执行_noul无criteria_应映射为空()
    {
        var (tool, context, service) = Create();
        var args = JsonDocument.Parse("""{"state":"s","questions":[{"id":"a","type":"noul","instructions":"x"}]}""").RootElement;

        await tool.ExecuteAsync(args, context);

        service.LastRequest!.Questions["a"].Criteria.Should().BeNull();
    }

    [Fact]
    public async Task 执行_取消_应向外抛出()
    {
        var service = new FakeSystemOneService { Throw = new OperationCanceledException() };
        var (tool, context, _) = Create(service);
        var args = JsonDocument.Parse("""{"state":"s","questions":[{"id":"a","type":"noul","instructions":"x"}]}""").RootElement;

        var act = async () => await tool.ExecuteAsync(args, context);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 执行_无provider的InvalidOperationException_应失败()
    {
        var service = new FakeSystemOneService { Throw = new InvalidOperationException("未配置可用的 SystemOne provider（检查 SYSTEMONE_API_KEY 与 systemone.json）") };
        var (tool, context, _) = Create(service);
        var args = JsonDocument.Parse("""{"state":"s","questions":[{"id":"a","type":"noul","instructions":"x"}]}""").RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("provider");
    }
}
