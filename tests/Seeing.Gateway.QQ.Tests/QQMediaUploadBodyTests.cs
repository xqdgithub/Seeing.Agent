using FluentAssertions;
using Seeing.Gateway.QQ.Connection;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQMediaUploadBodyTests
{
    [Fact]
    public void BuildUploadMediaBody_UrlMode_ShouldSetUrl()
    {
        var body = QQHttpApiClient.BuildUploadMediaBody(1, url: "https://x", fileData: null, fileName: null);
        body["file_type"].Should().Be(1);
        body["url"].Should().Be("https://x");
        body["srv_send_msg"].Should().Be(false);
    }

    [Fact]
    public void BuildUploadMediaBody_DataMode_ShouldSetFileData()
    {
        var body = QQHttpApiClient.BuildUploadMediaBody(4, url: null, fileData: "YWJj", fileName: "a.bin");
        body["file_data"].Should().Be("YWJj");
        body["file_name"].Should().Be("a.bin");
        body.Should().NotContainKey("url");
    }
}
