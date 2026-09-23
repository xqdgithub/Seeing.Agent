using FluentAssertions;
using Seeing.Agent.SystemOne.Configuration;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneEnvironmentConfigTests
{
    [Fact]
    public void Resolve_非空环境变量_应Trim后返回()
    {
        using var _ = SystemOneTestEnv.Scope(
            apiKey: "  ts-key  ", baseUrl: "  https://x.ai/  ",
            model: "  jev-x  ", provider: "  typesafe  ");

        SystemOneEnvironmentConfig.ResolveApiKey().Should().Be("ts-key");
        SystemOneEnvironmentConfig.ResolveBaseUrl().Should().Be("https://x.ai/");
        SystemOneEnvironmentConfig.ResolveModel().Should().Be("jev-x");
        SystemOneEnvironmentConfig.ResolveProvider().Should().Be("typesafe");
    }

    [Fact]
    public void Resolve_空白环境变量_应返回Null()
    {
        using var _ = SystemOneTestEnv.Scope(apiKey: "   ", baseUrl: "", model: "\t", provider: " ");

        SystemOneEnvironmentConfig.ResolveApiKey().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveBaseUrl().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveModel().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveProvider().Should().BeNull();
    }

    [Fact]
    public void Resolve_未设置环境变量_应返回Null()
    {
        using var _ = SystemOneTestEnv.Scope();

        SystemOneEnvironmentConfig.ResolveApiKey().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveBaseUrl().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveModel().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveProvider().Should().BeNull();
    }

    [Fact]
    public void 变量名常量_应为约定值()
    {
        SystemOneEnvironmentConfig.ApiKeyVar.Should().Be("SYSTEMONE_API_KEY");
        SystemOneEnvironmentConfig.BaseUrlVar.Should().Be("SYSTEMONE_BASE_URL");
        SystemOneEnvironmentConfig.ModelVar.Should().Be("SYSTEMONE_MODEL");
        SystemOneEnvironmentConfig.ProviderVar.Should().Be("SYSTEMONE_PROVIDER");
    }
}
