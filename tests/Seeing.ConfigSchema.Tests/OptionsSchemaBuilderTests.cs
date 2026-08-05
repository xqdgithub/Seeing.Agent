using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Seeing.ConfigSchema;
using Xunit;

namespace Seeing.ConfigSchema.Tests;

public class OptionsSchemaBuilderTests
{
    private sealed class SampleOptions
    {
        public bool Enabled { get; set; } = true;
        public string SectionName { get; set; } = "x";

        [Display(Name = "Bot Token", GroupName = "Auth")]
        [Required]
        public string ApiKey { get; set; } = "";

        [Display(Name = "Endpoint")]
        public string BaseUrl { get; set; } = "https://example.com";

        public int TimeoutMs { get; set; } = 30;

        public bool Verbose { get; set; }

        public SampleMode Mode { get; set; } = SampleMode.Fast;

        public List<string> Tags { get; set; } = new();

        [Browsable(false)]
        public string Hidden { get; set; } = "no";

        public Dictionary<string, string>? Unsupported { get; set; }
    }

    private enum SampleMode { Fast, Slow }

    [Fact]
    public void FromType_SkipsEnabledSectionNameBrowsableAndUnsupported()
    {
        var fields = OptionsSchemaBuilder.FromType(typeof(SampleOptions));
        fields.Select(f => f.Name).Should().NotContain(new[] { "Enabled", "SectionName", "Hidden", "Unsupported" });
    }

    [Fact]
    public void FromType_InfersSecretUrlNumberBooleanEnumStringList()
    {
        var map = OptionsSchemaBuilder.FromType(typeof(SampleOptions)).ToDictionary(f => f.Name);
        map["ApiKey"].Type.Should().Be(ConfigFieldType.Secret);
        map["ApiKey"].Required.Should().BeTrue();
        map["ApiKey"].Section.Should().Be("Auth");
        map["BaseUrl"].Type.Should().Be(ConfigFieldType.Url);
        map["TimeoutMs"].Type.Should().Be(ConfigFieldType.Number);
        map["Verbose"].Type.Should().Be(ConfigFieldType.Boolean);
        map["Mode"].Type.Should().Be(ConfigFieldType.Enum);
        map["Mode"].EnumValues.Should().Equal("Fast", "Slow");
        map["Tags"].Type.Should().Be(ConfigFieldType.StringList);
    }
}
