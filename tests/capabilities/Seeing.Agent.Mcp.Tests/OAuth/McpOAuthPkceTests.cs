using FluentAssertions;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

public class McpOAuthPkceTests
{
    [Fact]
    public void CreateCodeChallenge_ShouldMatchRfc7636S256Vector()
    {
        // RFC 7636 附录 B 的标准向量
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        var challenge = McpOAuthPkce.CreateCodeChallenge(verifier);

        challenge.Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void CreateCodeVerifier_ShouldBeUrlSafeAndHaveSufficientEntropy()
    {
        var verifier = McpOAuthPkce.CreateCodeVerifier();

        verifier.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        // RFC 7636 要求 code_verifier 长度 43-128
        verifier.Length.Should().BeInRange(43, 128);
    }

    [Fact]
    public void CreateCodeVerifier_ShouldProduceDistinctValues()
    {
        var first = McpOAuthPkce.CreateCodeVerifier();
        var second = McpOAuthPkce.CreateCodeVerifier();

        first.Should().NotBe(second);
    }
}
