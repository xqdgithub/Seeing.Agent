using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.WebUI.Workbench;
using Xunit;

namespace Seeing.Agent.WebUI.Workbench.Tests.Services;

/// <summary>
/// 历史工作台 id 别名：<c>markdown</c> 等价于 <c>files</c> + <c>preview=true</c>。
/// <para>
/// 保留别名的理由：模型已形成的调用习惯、历史对话里的工具调用记录、
/// 以及 <c>docs/architecture/41</c> 的示例都是 <c>workbenchId=markdown</c>。
/// 别放在宿主侧而不是工具侧——工具只查声明表，别名是「宿主怎么解释 id」的语义。
/// </para>
/// </summary>
public class LegacyWorkbenchAliasTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void Apply_LegacyMarkdownWithPath_ShouldBecomeFilesWithPreview()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply("markdown", Json("""{"path":"docs/a.md"}"""));

        id.Should().Be("files");
        payload!.Value.GetProperty("path").GetString().Should().Be("docs/a.md");
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Apply_LegacyMarkdownWithoutPayload_ShouldInjectPreviewOnly()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply("markdown", null);

        id.Should().Be("files");
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Apply_LegacyMarkdownWithExplicitPreviewFalse_ShouldRespectIt()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply(
            "markdown", Json("""{"path":"a.md","preview":false}"""));

        id.Should().Be("files");
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeFalse(
            "显式要求编辑态时不得被别名覆盖");
    }

    [Fact]
    public void Apply_FilesId_ShouldPassThroughUnchanged()
    {
        var original = Json("""{"path":"a.md","preview":false}""");

        var (id, payload) = LegacyWorkbenchAlias.Apply("files", original);

        id.Should().Be("files");
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Apply_OtherId_ShouldPassThroughUnchanged()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply("video", null);

        id.Should().Be("video");
        payload.Should().BeNull();
    }

    [Fact]
    public void Apply_LegacyMarkdownWithNonObjectPayload_ShouldStillInjectPreview()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply("markdown", Json("\"not-an-object\""));

        id.Should().Be("files");
        payload!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeTrue();
    }
}
