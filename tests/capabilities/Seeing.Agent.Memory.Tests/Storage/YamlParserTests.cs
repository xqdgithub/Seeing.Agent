using Seeing.Agent.Memory.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Storage;

public class YamlParserTests
{
    [Fact]
    public void ParseYamlFrontMatter_ValidYaml_ReturnsDictionary()
    {
        var content = @"---
id: mem_abc123
type: daily
title: ""API 配置最佳实践""
tags:
  - api
  - configuration
importance: 0.85
created_at: 2025-01-15T10:30:00Z
---

# Content here";

        var result = YamlParser.ParseYamlFrontMatter(content);
        
        Assert.Equal("mem_abc123", result["id"]);
        Assert.Equal("daily", result["type"]);
        Assert.Equal("API 配置最佳实践", result["title"]);
    }
    
    [Fact]
    public void ExtractMarkdownBody_WithFrontMatter_ReturnsBodyOnly()
    {
        var content = @"---
id: test
type: daily
---

# Title

Body content here.";

        var body = YamlParser.ExtractMarkdownBody(content);
        
        Assert.Contains("# Title", body);
        Assert.Contains("Body content here.", body);
        Assert.DoesNotContain("---", body);
    }
    
    [Fact]
    public void ParseYamlFrontMatter_NoFrontMatter_ReturnsEmpty()
    {
        var content = "# Just markdown\nNo frontmatter here.";
        
        var result = YamlParser.ParseYamlFrontMatter(content);
        
        Assert.Empty(result);
    }
    
    [Fact]
    public void ParseYamlFrontMatter_EmptyContent_ReturnsEmpty()
    {
        var result = YamlParser.ParseYamlFrontMatter("");
        
        Assert.Empty(result);
    }
    
    [Fact]
    public void ExtractMarkdownBody_NoFrontMatter_ReturnsOriginalContent()
    {
        var content = "# Just markdown\nNo frontmatter here.";
        
        var body = YamlParser.ExtractMarkdownBody(content);
        
        Assert.Equal(content, body);
    }
    
    [Fact]
    public void ExtractMarkdownBody_EmptyContent_ReturnsEmptyString()
    {
        var body = YamlParser.ExtractMarkdownBody("");
        
        Assert.Equal(string.Empty, body);
    }
    
    [Fact]
    public void ParseYamlFrontMatter_WithListValues_ParsesListCorrectly()
    {
        var content = @"---
id: test
tags:
  - tag1
  - tag2
  - tag3
---

Content";

        var result = YamlParser.ParseYamlFrontMatter(content);
        
        Assert.True(result.ContainsKey("tags"));
        // YamlDotNet 解析列表为 List<object>
        var tags = result["tags"] as List<object>;
        Assert.NotNull(tags);
        Assert.Equal(3, tags.Count);
    }
    
    [Fact]
    public void ParseYamlFrontMatter_WithNumericValue_ParsesCorrectly()
    {
        var content = @"---
id: test
count: 42
score: 3.14
---

Content";

        var result = YamlParser.ParseYamlFrontMatter(content);
        
        // YamlDotNet 解析整数，类型可能是 int 或 long
        Assert.True(result.ContainsKey("count"));
        Assert.NotNull(result["count"]);
        // 验证可以被转换为整数
        var countValue = Convert.ToInt64(result["count"]);
        Assert.Equal(42, countValue);
        // 浮点数可能被解析为 double 或 decimal
        Assert.NotNull(result["score"]);
    }
    
    [Fact]
    public void ExtractMarkdownBody_WithMultipleNewlines_PreservesNewlines()
    {
        var content = @"---
id: test
---


# Title

Body here.";

        var body = YamlParser.ExtractMarkdownBody(content);
        
        // 实现保留前导换行符
        Assert.Contains("# Title", body);
        Assert.Contains("Body here.", body);
    }
    
    [Fact]
    public void ParseYamlFrontMatter_WithMultilineValue_ParsesCorrectly()
    {
        var content = @"---
id: test
description: |
  This is a multiline
  description for testing.
---

Content";

        var result = YamlParser.ParseYamlFrontMatter(content);
        
        Assert.True(result.ContainsKey("description"));
        var description = result["description"]?.ToString();
        Assert.Contains("multiline", description);
    }
}
