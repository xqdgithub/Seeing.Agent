using System.Net;
using FluentAssertions;
using Seeing.Gateway.QQ.Connection;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQApiExceptionTests
{
    [Fact]
    public void IsUrlContentError_ShouldDetectKnownCodes()
    {
        var ex = new QQApiException(HttpStatusCode.BadRequest, "/msg", "{\"code\":304003,\"message\":\"不允许包含url\"}");
        ex.IsUrlContentError.Should().BeTrue();
    }

    [Fact]
    public void IsMarkdownValidationError_ShouldDetectMarkdownCodes()
    {
        var ex = new QQApiException(HttpStatusCode.BadRequest, "/msg", "{\"err_code\":40034012,\"message\":\"markdown\"}");
        ex.IsMarkdownValidationError.Should().BeTrue();
    }
}
