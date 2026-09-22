using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class GatewayConfigTests
{
    [Fact]
    public void Resolve_WithoutConfigFile_ShouldReportDisabled()
    {
        using var temp = new TempWorkspace();

        var (enabled, port) = GatewayConfig.Resolve(temp.Path);

        enabled.Should().BeFalse();
        port.Should().Be(GatewayConfig.DefaultPort);
    }

    [Fact]
    public void Resolve_WithEnabledGateway_ShouldReportEnabledAndConfiguredPort()
    {
        using var temp = new TempWorkspace();
        temp.WriteSeeingJson("""
            {
              "SeeingAgent": {
                "Gateway": { "Enabled": true, "Port": 9001 }
              }
            }
            """);

        var (enabled, port) = GatewayConfig.Resolve(temp.Path);

        enabled.Should().BeTrue();
        port.Should().Be(9001);
    }

    [Fact]
    public void Resolve_WithGatewayDisabled_ShouldReportDisabled()
    {
        using var temp = new TempWorkspace();
        temp.WriteSeeingJson("""
            { "SeeingAgent": { "Gateway": { "Enabled": false } } }
            """);

        GatewayConfig.Resolve(temp.Path).Enabled.Should().BeFalse();
    }

    [Fact]
    public void Resolve_WithoutGatewaySection_ShouldReportDisabled()
    {
        using var temp = new TempWorkspace();
        temp.WriteSeeingJson("""{ "SeeingAgent": { "Permission": { "AutoApproveAll": true } } }""");

        GatewayConfig.Resolve(temp.Path).Enabled.Should().BeFalse();
    }

    [Fact]
    public void Resolve_WithCamelCaseKeys_ShouldStillMatch()
    {
        using var temp = new TempWorkspace();
        temp.WriteSeeingJson("""{ "seeingAgent": { "gateway": { "enabled": true, "port": 9100 } } }""");

        var (enabled, port) = GatewayConfig.Resolve(temp.Path);

        enabled.Should().BeTrue();
        port.Should().Be(9100);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("\"not-a-number\"")]
    public void Resolve_WithInvalidPort_ShouldFallBackToDefault(string portJson)
    {
        using var temp = new TempWorkspace();
        temp.WriteSeeingJson($$"""
            { "SeeingAgent": { "Gateway": { "Enabled": true, "Port": {{portJson}} } } }
            """);

        GatewayConfig.Resolve(temp.Path).Port.Should().Be(GatewayConfig.DefaultPort);
    }

    [Fact]
    public void Resolve_WithMalformedJson_ShouldReportDisabled()
    {
        using var temp = new TempWorkspace();
        temp.WriteSeeingJson("{ \"SeeingAgent\": { \"Gateway\": ");

        var (enabled, port) = GatewayConfig.Resolve(temp.Path);

        enabled.Should().BeFalse();
        port.Should().Be(GatewayConfig.DefaultPort);
    }

    [Fact]
    public void BuildDisabledMessage_ShouldPointToProjectConfigPath()
    {
        using var temp = new TempWorkspace();

        var message = GatewayConfig.BuildDisabledMessage(temp.Path);

        message.Should().Contain(GatewayConfig.ResolveConfigPath(temp.Path));
        message.Should().Contain("Enabled");
    }

    /// <summary>只写 <c>seeing.json</c>，文件内容由 JSON 片段决定（含故意损坏的片段）。</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "seeing-gateway-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".seeing"));
        }

        public string Path { get; }

        public void WriteSeeingJson(string content)
            => File.WriteAllText(GatewayConfig.ResolveConfigPath(Path), content);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结论。
            }
        }
    }
}
