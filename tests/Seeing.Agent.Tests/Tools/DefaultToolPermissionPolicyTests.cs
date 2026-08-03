// tests/Seeing.Agent.Tests/Tools/DefaultToolPermissionPolicyTests.cs

using FluentAssertions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Tools.BuiltIn;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class DefaultToolPermissionPolicyTests
{
    private readonly DefaultToolPermissionPolicy _policy = new();

    [Fact]
    public void Evaluate_UnknownTool_ReturnsNull()
    {
        var args = JsonSerializer.SerializeToElement(new { });
        _policy.Evaluate("nonexistent", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_ReadTool_ReturnsFilesystemReadWithFilePath()
    {
        var args = JsonSerializer.SerializeToElement(new { file_path = "/home/user/readme.md" });
        var check = _policy.Evaluate("read", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.read");
        check.Resource.Should().Be("/home/user/readme.md");
        check.Patterns.Should().BeNull();
        check.Metadata.Should().ContainKey("file_path");
        check.Metadata!["file_path"].Should().Be("/home/user/readme.md");
    }

    [Fact]
    public void Evaluate_WriteTool_ReturnsFilesystemWriteWithFilePath()
    {
        var args = JsonSerializer.SerializeToElement(new { file_path = "/tmp/output.txt" });
        var check = _policy.Evaluate("write", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.write");
        check.Resource.Should().Be("/tmp/output.txt");
    }

    [Fact]
    public void Evaluate_EditTool_ReturnsFilesystemWriteWithFilePath()
    {
        var args = JsonSerializer.SerializeToElement(new { file_path = "/app/config.json" });
        var check = _policy.Evaluate("edit", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.write");
        check.Resource.Should().Be("/app/config.json");
    }

    [Fact]
    public void Evaluate_GrepTool_ReturnsFilesystemReadWithPath()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/src" });
        var check = _policy.Evaluate("grep", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.read");
        check.Resource.Should().Be("/src");
    }

    [Fact]
    public void Evaluate_GlobTool_ReturnsFilesystemReadWithPath()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/src" });
        var check = _policy.Evaluate("glob", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.read");
        check.Resource.Should().Be("/src");
    }

    [Fact]
    public void Evaluate_BashTool_ReturnsShellExecuteWithCommand()
    {
        var args = JsonSerializer.SerializeToElement(new { command = "ls -la" });
        var check = _policy.Evaluate("bash", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("shell.execute");
        check.Resource.Should().Be("ls -la");
        check.Metadata.Should().ContainKey("command");
        check.Metadata!["command"].Should().Be("ls -la");
    }

    [Fact]
    public void Evaluate_WebFetch_ReturnsNetworkFetchWithUrl()
    {
        var args = JsonSerializer.SerializeToElement(new { url = "https://example.com" });
        var check = _policy.Evaluate("webfetch", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("network.fetch");
        check.Resource.Should().Be("https://example.com");
        check.Metadata.Should().ContainKey("url");
        check.Metadata!["url"].Should().Be("https://example.com");
    }

    [Fact]
    public void Evaluate_WebSearch_ReturnsNetworkSearchWithFixedResource()
    {
        var args = JsonSerializer.SerializeToElement(new { query = "test" });
        var check = _policy.Evaluate("websearch", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("network.search");
        check.Resource.Should().Be("web_search");
        check.Metadata.Should().ContainKey("query");
        check.Metadata!["query"].Should().Be("test");
    }

    [Fact]
    public void Evaluate_CodeSearch_ReturnsNetworkSearchWithFixedResource()
    {
        var args = JsonSerializer.SerializeToElement(new { query = "test" });
        var check = _policy.Evaluate("codesearch", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("network.search");
        check.Resource.Should().Be("code_search");
    }

    [Fact]
    public void Evaluate_AddWorkspacePath_ReturnsWorkspaceExtendWithPatterns()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/new/project" });
        var check = _policy.Evaluate("add_workspace_path", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.workspace_extend");
        check.Resource.Should().Be("/new/project/"); // trailing separator added
        check.Patterns.Should().NotBeNull();
        check.Patterns!.Should().Contain("/new/project");
        check.Metadata.Should().NotBeNull();
        check.Metadata!["reason"].Should().Be("Agent 请求扩展工作区路径");
        check.Metadata!["path"].Should().Be("/new/project");
    }

    [Fact]
    public void Evaluate_AddWorkspacePath_AlreadyTrailingSeparator_NotDoubled()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/existing/" });
        var check = _policy.Evaluate("add_workspace_path", args);
        check!.Resource.Should().Be("/existing/"); // not doubled
    }

    [Fact]
    public void Evaluate_TodoWrite_ReturnsNull_NoResourceCheck()
    {
        var args = JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "x", status = "pending" } } });
        _policy.Evaluate("todowrite", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_CurrentTime_ReturnsNull_NoResourceCheck()
    {
        var args = JsonSerializer.SerializeToElement(new { });
        _policy.Evaluate("current_time", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_PlanEnter_ReturnsNull_NoResourceCheck()
    {
        var args = JsonSerializer.SerializeToElement(new { name = "plan" });
        _policy.Evaluate("plan_enter", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_Task_ReturnsNull_NoResourceCheck()
    {
        var args = JsonSerializer.SerializeToElement(new { description = "x", prompt = "y", subagent_type = "z" });
        _policy.Evaluate("task", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_MissingArgKey_ReturnsEmptyResource()
    {
        // grep with no "path" arg -- resource is empty string
        var args = JsonSerializer.SerializeToElement(new { pattern = "something" });
        var check = _policy.Evaluate("grep", args);
        check.Should().NotBeNull();
        check!.Resource.Should().Be(string.Empty);
    }

    [Fact]
    public void Evaluate_NonObjectArgs_ReturnsEmptyResource()
    {
        // args that are a scalar, not an object
        var args = JsonSerializer.SerializeToElement("just a string");
        var check = _policy.Evaluate("read", args);
        check.Should().NotBeNull();
        check!.Resource.Should().Be(string.Empty);
    }

    [Fact]
    public void Evaluate_ReadTool_MetadataIncludesAllArgs()
    {
        var args = JsonSerializer.SerializeToElement(new { file_path = "/a.txt", offset = 10, limit = 50 });
        var check = _policy.Evaluate("read", args);
        check.Should().NotBeNull();
        check!.Metadata.Should().ContainKey("file_path");
        check.Metadata!["file_path"].Should().Be("/a.txt");
        check.Metadata.Should().ContainKey("offset");
        check.Metadata!["offset"].Should().Be(10);
        check.Metadata.Should().ContainKey("limit");
        check.Metadata!["limit"].Should().Be(50);
    }
}
