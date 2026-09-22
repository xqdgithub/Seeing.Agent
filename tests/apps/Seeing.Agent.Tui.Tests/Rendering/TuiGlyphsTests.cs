using System.Reflection;
using System.Text;
using FluentAssertions;
using Seeing.Agent.Tui.Rendering;

namespace Seeing.Agent.Tui.Tests.Rendering;

public sealed class TuiGlyphsTests
{
    // 系统控制台默认代码页 936(GBK)：字形必须能在该编码下无损往返，否则被编码器回退成字面 ?。
    private static readonly Encoding Gbk = CreateGbk();

    private static Encoding CreateGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    public static TheoryData<string, string> GlyphFields()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in EnumerateGlyphs())
            data.Add(field.Name, (string)field.GetRawConstantValue()!);
        return data;
    }

    [Fact]
    public void AllGlyphs_ShouldExposeAtLeastOneConstant()
        => EnumerateGlyphs().Should().NotBeEmpty();

    [Theory]
    [MemberData(nameof(GlyphFields))]
    public void Glyph_ShouldRoundTripUnderGbk(string name, string value)
    {
        Gbk.GetString(Gbk.GetBytes(value))
            .Should().Be(value, $"字形常量 {name} 在 CP936 下必须可无损往返，否则会渲染为 ?");
    }

    [Theory]
    [InlineData("√")]
    [InlineData("×")]
    [InlineData("·")]
    [InlineData("…")]
    [InlineData("─")]
    [InlineData("▌")]
    [InlineData("●")]
    [InlineData("○")]
    [InlineData("◆")]
    [InlineData("★")]
    [InlineData("→")]
    public void KnownSafeGlyph_ShouldRoundTripUnderGbk(string value)
        => Gbk.GetString(Gbk.GetBytes(value)).Should().Be(value);

    [Fact]
    public void GlyphConstants_ShouldUseExpectedGbkSafeValues()
    {
        typeof(TuiGlyphs).GetField(nameof(TuiGlyphs.Tool))!.GetRawConstantValue().Should().Be("●");
        typeof(TuiGlyphs).GetField(nameof(TuiGlyphs.Success))!.GetRawConstantValue().Should().Be("√");
        typeof(TuiGlyphs).GetField(nameof(TuiGlyphs.Failure))!.GetRawConstantValue().Should().Be("×");
        typeof(TuiGlyphs).GetField(nameof(TuiGlyphs.Cursor))!.GetRawConstantValue().Should().Be("▌");
        typeof(TuiGlyphs).GetField(nameof(TuiGlyphs.Divider))!.GetRawConstantValue().Should().Be("─");
        typeof(TuiGlyphs).GetField(nameof(TuiGlyphs.Bullet))!.GetRawConstantValue().Should().Be("·");
        typeof(TuiGlyphs).GetField(nameof(TuiGlyphs.Arrow))!.GetRawConstantValue().Should().Be("→");
    }

    private static IEnumerable<FieldInfo> EnumerateGlyphs()
        => typeof(TuiGlyphs)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string));
}
