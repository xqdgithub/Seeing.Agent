using FluentAssertions;
using System.Text.RegularExpressions;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 语音输入按钮的图标语义守门。
/// <para>
/// antd 图标库里 <c>Sound</c> 是<b>喇叭（输出）</b>、<c>Audio</c> 是<b>麦克风（输入）</b>。
/// 两者都叫「声音」，很容易被误改成 <c>Sound</c>——那样按钮看起来像「播放」，
/// 用户不会想到点它说话。图标选错不会编译失败、也不会让任何常规测试变红，
/// 因此在此按源码断言。
/// </para>
/// </summary>
public class PromptInputVoiceIconTests
{
    [Fact]
    public void VoiceButton_Should_Use_Microphone_Icon_Not_Speaker()
    {
        var source = ReadWebUiSource("Components/PromptInput.razor");

        // 定位语音按钮所在片段：从 prompt-voice-btn 到其后的第一个 Icon 声明。
        var voiceBlock = ExtractVoiceButtonBlock(source);
        voiceBlock.Should().NotBeEmpty("PromptInput 里应有语音按钮区块");

        voiceBlock.Should().Contain("IconType.Outline.Audio",
            "麦克风图标：Audio 才是输入设备；Sound 是喇叭（输出）");

        voiceBlock.Should().NotContain("IconType.Outline.Sound",
            "Sound 是喇叭，用在语音输入按钮上会让人以为这是播放按钮");
    }

    /// <summary>从 <c>prompt-voice-btn</c> 起截取到该 Button 结束的片段。</summary>
    private static string ExtractVoiceButtonBlock(string source)
    {
        var start = source.IndexOf("prompt-voice-btn", StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        // 按钮区块在 SpaceItem 内结束；取足够覆盖 Icon 声明的一段即可。
        var end = source.IndexOf("</SpaceItem>", start, StringComparison.Ordinal);
        return end < 0
            ? source[start..]
            : source[start..end];
    }

    /// <summary>
    /// 读取 WebUI 源码。测试运行时输出目录不含源码，需要回溯到仓库内的 samples 目录。
    /// </summary>
    private static string ReadWebUiSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "samples", "Seeing.Agent.WebUI", relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 WebUI 源文件：{relativePath}");
    }
}
