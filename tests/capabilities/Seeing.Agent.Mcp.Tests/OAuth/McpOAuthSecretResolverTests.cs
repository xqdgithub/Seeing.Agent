using FluentAssertions;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

/// <summary>
/// ClientSecret 环境变量引用解析：支持 <c>env:VAR</c> 与 <c>${VAR}</c>，
/// 使配置文件只承载引用而非明文密钥。
/// </summary>
public class McpOAuthSecretResolverTests
{
    [Theory]
    [InlineData("env:MY_SECRET", true)]
    [InlineData("${MY_SECRET}", true)]
    [InlineData("literal-secret", false)]
    [InlineData("env:", false)]
    [InlineData("${}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEnvironmentReference_ShouldDetectReferenceSyntax(string? value, bool expected)
        => McpOAuthSecretResolver.IsEnvironmentReference(value).Should().Be(expected);

    [Fact]
    public void Resolve_WithEnvPrefix_ShouldReadEnvironmentVariable()
    {
        var name = "SEEING_MCP_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "resolved-env");
        try
        {
            McpOAuthSecretResolver.Resolve($"env:{name}").Should().Be("resolved-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Resolve_WithBraceSyntax_ShouldReadEnvironmentVariable()
    {
        var name = "SEEING_MCP_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "resolved-brace");
        try
        {
            McpOAuthSecretResolver.Resolve($"${{{name}}}").Should().Be("resolved-brace");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Resolve_WithLiteral_ShouldReturnAsIs()
        => McpOAuthSecretResolver.Resolve("plain-secret").Should().Be("plain-secret");

    [Fact]
    public void Resolve_WithUnsetEnvironmentVariable_ShouldReturnNull()
        => McpOAuthSecretResolver.Resolve("env:SEEING_MCP_TEST_DEFINITELY_MISSING").Should().BeNull();
}
