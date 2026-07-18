using Seeing.Agent.Memory.Core.Storage;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Storage;

public class WikilinkParserTests
{
    [Fact]
    public void Parse_SingleWikilink_ReturnsSingleLink()
    {
        var content = "相关文档: [[digest/wiki/rest-api-design]]";
        
        var links = WikilinkParser.Parse(content);
        
        Assert.Single(links);
        Assert.Equal("digest/wiki/rest-api-design", links[0]);
    }
    
    [Fact]
    public void Parse_MultipleWikilinks_ReturnsAllLinks()
    {
        var content = @"
参考: [[daily/2025-01-10/api-debug-session]]
相关: [[digest/wiki/rest-api-design]]
";
        
        var links = WikilinkParser.Parse(content);
        
        Assert.Equal(2, links.Count);
        Assert.Contains("daily/2025-01-10/api-debug-session", links);
        Assert.Contains("digest/wiki/rest-api-design", links);
    }
    
    [Fact]
    public void Parse_WikilinkWithAnchor_ExtractsPathOnly()
    {
        var content = "参见: [[digest/wiki/api#configuration]]";
        
        var links = WikilinkParser.Parse(content);
        
        Assert.Single(links);
        Assert.Equal("digest/wiki/api", links[0]);
    }
    
    [Fact]
    public void Parse_NoWikilink_ReturnsEmptyList()
    {
        var content = "这是一段普通文本，没有链接。";
        
        var links = WikilinkParser.Parse(content);
        
        Assert.Empty(links);
    }
}
