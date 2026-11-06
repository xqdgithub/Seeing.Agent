using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Jobs;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>
/// 旁路目录补偿：目录已迁为 <c>Target = Message</c> 的上下文源，旁路只读装配不插消息，
/// 因此必须把同一结算产出的目录正文拼进系统基线，否则 cron/heartbeat 模型失去目录。
/// </summary>
public class AgentJobCatalogBaselineTests
{
    [Fact]
    public void 有目录正文_应拼到基线末尾()
    {
        var resolution = BuildResolution("CATALOG");

        var result = AgentJob.AppendCatalogToBaseline("BASE", resolution);

        result.Should().Be("BASE\n\nCATALOG");
    }

    [Fact]
    public void 基线为空_应只保留目录正文()
    {
        var resolution = BuildResolution("CATALOG");

        AgentJob.AppendCatalogToBaseline(string.Empty, resolution).Should().Be("CATALOG");
    }

    [Fact]
    public void 无目录正文_基线应原样返回()
    {
        var resolution = BuildResolution(null);

        AgentJob.AppendCatalogToBaseline("BASE", resolution).Should().Be("BASE");
    }

    [Fact]
    public void 结算缺失_基线应原样返回()
    {
        // 解析器未注册时 resolution 为 null（旁路降级路径），不得抛异常。
        AgentJob.AppendCatalogToBaseline("BASE", null).Should().Be("BASE");
    }

    private static SessionToolSchemaResolution BuildResolution(string? catalogText) =>
        new(
            Array.Empty<ExposedTool>(),
            Array.Empty<string>(),
            ToolExposureLayout.Tiered,
            catalogText,
            SchemaTokens: 0,
            CatalogTokens: 0);
}
