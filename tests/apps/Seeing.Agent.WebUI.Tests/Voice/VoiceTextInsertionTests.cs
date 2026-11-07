using FluentAssertions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Tests.Voice;

public class VoiceTextInsertionTests
{
    [Fact]
    public void Insert_Should_Replace_Selection_And_Place_Caret_After_Text()
    {
        var (text, caret) = VoiceTextInsertion.Insert("你好世界", 2, 4, "语音");

        text.Should().Be("你好语音");
        caret.Should().Be(4);
    }

    [Fact]
    public void Insert_Should_Add_Spaces_When_Surrounded_By_Word_Characters()
    {
        var (text, _) = VoiceTextInsertion.Insert("abcdef", 3, 3, "语音");

        text.Should().Be("abc 语音 def");
    }

    [Fact]
    public void Insert_Should_Not_Double_Space_When_Already_After_Whitespace()
    {
        // 光标已在空格之后（位置 4），左侧不再补空格；右侧贴 ASCII 词，补一个。
        var (text, _) = VoiceTextInsertion.Insert("abc def", 4, 4, "语音");

        text.Should().Be("abc 语音 def");
    }

    [Fact]
    public void Insert_Should_Not_Add_Left_Space_After_Opening_Bracket()
    {
        // 左括号不是 ASCII 词字符，不补空格
        var (text, _) = VoiceTextInsertion.Insert("（", 1, 1, "语音");

        text.Should().Be("（语音");
    }

    [Fact]
    public void Insert_Should_Not_Add_Right_Space_Before_Closing_Bracket()
    {
        var (text, _) = VoiceTextInsertion.Insert(")", 0, 0, "语音");

        text.Should().Be("语音)");
    }

    [Fact]
    public void Insert_Should_Return_Original_When_Insert_Is_Whitespace()
    {
        var (text, caret) = VoiceTextInsertion.Insert("abc", 1, 1, "   ");

        text.Should().Be("abc");
        caret.Should().Be(1);
    }

    [Fact]
    public void Insert_Should_Clamp_Out_Of_Range_Anchors()
    {
        var (text, caret) = VoiceTextInsertion.Insert("ab", 99, 100, "x");

        text.Should().Be("abx");
        caret.Should().Be(3);
    }
}
