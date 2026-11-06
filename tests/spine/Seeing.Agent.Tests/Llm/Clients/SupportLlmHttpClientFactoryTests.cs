using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Http;
using System.Net;
using Xunit;

namespace Seeing.Agent.Tests.Llm.Clients;

/// <summary>
/// 统一 HTTP 工厂的代理语义守门（原 OpenAI / Anthropic 各有一份重复实现，现上提到 Support）。
/// <para><b>为什么这条不能靠「能编译」保证</b>：代理配错的表现是「网络慢/连不上」，
/// 而不是报错。用户在墙内调 LLM 时，代理失效等于整个功能不可用，且看不出原因。</para>
/// </summary>
public class SupportLlmHttpClientFactoryTests
{
    [Fact]
    public void CreateHandler_WithoutExplicitProxy_ShouldUseSystemProxy()
    {
        // 默认 UseProxy=true 且 Proxy=null → handler 读操作系统代理设置
        using var handler = LlmHttpClientFactory.CreateHandler(new ProviderConfig());

        handler.UseProxy.Should().BeTrue("ProviderConfig.UseProxy 默认 true，须继承系统代理");
        handler.Proxy.Should().BeNull("null 才会让 SocketsHttpHandler 回落到系统设置");
    }

    [Fact]
    public void CreateHandler_WithExplicitProxy_ShouldOverrideSystemProxy()
    {
        using var handler = LlmHttpClientFactory.CreateHandler(new ProviderConfig
        {
            Proxy = "http://proxy.example:8080"
        });

        handler.UseProxy.Should().BeTrue();
        var proxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
        proxy.Address.Should().Be(new Uri("http://proxy.example:8080"));
    }

    [Fact]
    public void CreateHandler_WithCredentials_ShouldStripUserInfoFromAddress()
    {
        using var handler = LlmHttpClientFactory.CreateHandler(new ProviderConfig
        {
            Proxy = "https://user:p%40ss@proxy.example:8443"
        });

        var proxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
        proxy.Address.Should().Be(new Uri("https://proxy.example:8443"),
            "凭据不能留在 URL 里，否则会作为路径一部分发给代理");
        var credential = proxy.Credentials.Should().BeOfType<NetworkCredential>().Subject;
        credential.UserName.Should().Be("user");
        credential.Password.Should().Be("p@ss");
    }

    [Fact]
    public void CreateHandler_WhenProxyDisabled_ShouldIgnoreExplicitProxy()
    {
        using var handler = LlmHttpClientFactory.CreateHandler(new ProviderConfig
        {
            UseProxy = false,
            Proxy = "http://proxy.example:8080"
        });

        handler.UseProxy.Should().BeFalse("显式关闭代理时不能被 Proxy 字段重新打开");
        handler.Proxy.Should().BeNull();
    }

    [Fact]
    public void CreateHandler_WithSocksProxy_ShouldRejectWithReadableError()
    {
        // socks 不支持时必须报错，不能静默直连（静默直连会让用户以为代理生效了）
        var act = () => LlmHttpClientFactory.CreateHandler(new ProviderConfig
        {
            Proxy = "socks5://127.0.0.1:1080"
        });

        act.Should().Throw<ArgumentException>().WithMessage("*http or https*");
    }

    [Fact]
    public void Create_ShouldReturnClientWithHandlerAttached()
    {
        using var client = LlmHttpClientFactory.Create(new ProviderConfig
        {
            Proxy = "http://proxy.example:8080"
        });

        client.Should().NotBeNull();
    }

    /// <summary>
    /// 协议包内的转发壳必须与统一实现行为一致——否则「上提」只是多了一层，
    /// OpenAI/Anthropic 的真实请求仍走各自旧逻辑。
    /// </summary>
    [Fact]
    public void ProtocolPackageShells_ShouldDelegateToSupportImplementation()
    {
        using var openAi = Seeing.Agent.Llm.OpenAI.Clients.LlmHttpClientFactory
            .CreateHandler(new ProviderConfig { Proxy = "http://proxy.example:8080" });
        using var anthropic = Seeing.Agent.Llm.Anthropic.Clients.LlmHttpClientFactory
            .CreateHandler(new ProviderConfig { Proxy = "http://proxy.example:8080" });

        ((WebProxy)openAi.Proxy!).Address.Should().Be(new Uri("http://proxy.example:8080"));
        ((WebProxy)anthropic.Proxy!).Address.Should().Be(new Uri("http://proxy.example:8080"));
    }
}
