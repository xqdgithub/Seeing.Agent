using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Generation;
using Xunit;

namespace Seeing.Agent.Tests.Generation;

public class AgentTemplateEngineTests
{
    private readonly AgentTemplateEngine _engine = new();

    [Fact]
    public void Render_SimpleVariable_ShouldReplace()
    {
        // Arrange
        var template = "Hello, {{name}}!";
        var variables = new Dictionary<string, string> { ["name"] = "World" };

        // Act
        var result = _engine.Render(template, variables);

        // Assert
        result.RenderedContent.Should().Be("Hello, World!");
        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Render_MultipleVariables_ShouldReplaceAll()
    {
        // Arrange
        var template = "{{greeting}}, {{name}}! Welcome to {{place}}.";
        var variables = new Dictionary<string, string>
        {
            ["greeting"] = "Hello",
            ["name"] = "Alice",
            ["place"] = "Wonderland"
        };

        // Act
        var result = _engine.Render(template, variables);

        // Assert
        result.RenderedContent.Should().Be("Hello, Alice! Welcome to Wonderland.");
    }

    [Fact]
    public void Render_WithDefaultValue_ShouldUseDefault()
    {
        // Arrange
        var template = "Hello, {{name:Guest}}!";
        var variables = new Dictionary<string, string>();

        // Act
        var result = _engine.Render(template, variables);

        // Assert
        result.RenderedContent.Should().Be("Hello, Guest!");
    }

    [Fact]
    public void Render_WithValueAndDefault_ShouldUseValue()
    {
        // Arrange
        var template = "Hello, {{name:Guest}}!";
        var variables = new Dictionary<string, string> { ["name"] = "Alice" };

        // Act
        var result = _engine.Render(template, variables);

        // Assert
        result.RenderedContent.Should().Be("Hello, Alice!");
    }

    [Fact]
    public void Render_MissingVariableWithoutDefault_ShouldWarn()
    {
        // Arrange
        var template = "Hello, {{name}}!";
        var variables = new Dictionary<string, string>();

        // Act
        var result = _engine.Render(template, variables);

        // Assert
        result.Warnings.Should().Contain(w => w.Contains("name"));
        result.RenderedContent.Should().Contain("{{name}}");
    }

    [Fact]
    public void ExtractVariables_ShouldFindAll()
    {
        // Arrange
        var template = "{{name}} and {{age}} and {{city:Unknown}}";

        // Act
        var variables = _engine.ExtractVariables(template);

        // Assert
        variables.Should().HaveCount(3);
        variables.Should().Contain(v => v.Name == "name" && !v.HasDefaultValue);
        variables.Should().Contain(v => v.Name == "age" && !v.HasDefaultValue);
        variables.Should().Contain(v => v.Name == "city" && v.HasDefaultValue && v.DefaultValue == "Unknown");
    }

    [Fact]
    public void ValidateTemplate_ValidTemplate_ShouldPass()
    {
        // Arrange
        var template = "Hello, {{name}}!";

        // Act
        var result = _engine.ValidateTemplate(template);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateTemplate_UnclosedPlaceholder_ShouldFail()
    {
        // Arrange
        var template = "Hello, {{name!";

        // Act
        var result = _engine.ValidateTemplate(template);

        // Assert
        result.IsValid.Should().BeFalse();
    }
}

public class AgentValidatorTests
{
    private readonly AgentValidator _validator = new();

    [Fact]
    public void Validate_ValidDefinition_ShouldPass()
    {
        // Arrange
        var definition = new AgentDefinition
        {
            Name = "test-agent",
            Description = "Test agent",
            SystemPrompt = "You are a test agent.",
            MaxIterations = 10,
            TimeoutSeconds = 300
        };

        // Act
        var result = _validator.Validate(definition);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyName_ShouldFail()
    {
        // Arrange
        var definition = new AgentDefinition
        {
            Name = "",
            SystemPrompt = "Test"
        };

        // Act
        var result = _validator.Validate(definition);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("name"));
    }

    [Fact]
    public void Validate_ReservedName_ShouldFail()
    {
        // Arrange
        var definition = new AgentDefinition
        {
            Name = "system",
            SystemPrompt = "Test"
        };

        // Act
        var result = _validator.Validate(definition);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_InvalidNameFormat_ShouldFail()
    {
        // Arrange
        var definition = new AgentDefinition
        {
            Name = "123-invalid", // Must start with letter
            SystemPrompt = "Test"
        };

        // Act
        var result = _validator.Validate(definition);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ToolConflict_ShouldFail()
    {
        // Arrange
        var definition = new AgentDefinition
        {
            Name = "test-agent",
            SystemPrompt = "Test",
            AllowedTools = new List<string> { "read", "write" },
            DeniedTools = new List<string> { "write" } // Conflict!
        };

        // Act
        var result = _validator.Validate(definition);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("write"));
    }

    [Fact]
    public void Validate_InvalidTemperature_ShouldFail()
    {
        // Arrange
        var definition = new AgentDefinition
        {
            Name = "test-agent",
            SystemPrompt = "Test",
            ModelConfig = new ModelConfigOverride { Temperature = 3.0 } // Invalid: > 2
        };

        // Act
        var result = _validator.Validate(definition);

        // Assert
        result.IsValid.Should().BeFalse();
    }
}

public class AgentGeneratorTests
{
    [Fact]
    public async Task ListTemplatesAsync_ShouldReturnBuiltinTemplates()
    {
        // Arrange
        var generator = CreateGenerator();

        // Act
        var templates = await generator.ListTemplatesAsync();

        // Assert
        templates.Should().Contain(t => t.Name == "general-assistant");
        templates.Should().Contain(t => t.Name == "code-expert");
        templates.Should().Contain(t => t.Name == "researcher");
        templates.Should().Contain(t => t.Name == "reviewer");
    }

    [Fact]
    public async Task GenerateAsync_WithTemplate_ShouldRenderVariables()
    {
        // Arrange
        var generator = CreateGenerator();
        var request = new AgentGenerationRequest
        {
            TemplateId = "general-assistant",
            Name = "my-assistant",
            Description = "My custom assistant",
            Variables = new Dictionary<string, string>
            {
                ["Name"] = "CustomBot",
                ["Description"] = "A custom bot for testing"
            }
        };

        // Act
        var definition = await generator.GenerateAsync(request);

        // Assert
        definition.Name.Should().Be("my-assistant");
        definition.SystemPrompt.Should().Contain("CustomBot");
        definition.SourceTemplateId.Should().Be("general-assistant");
    }

    [Fact]
    public async Task GenerateAsync_WithoutTemplate_ShouldUseDefaults()
    {
        // Arrange
        var generator = CreateGenerator();
        var request = new AgentGenerationRequest
        {
            Name = "simple-agent",
            Description = "Simple test",
            SystemPrompt = "You are simple."
        };

        // Act
        var definition = await generator.GenerateAsync(request);

        // Assert
        definition.Name.Should().Be("simple-agent");
        definition.SystemPrompt.Should().Be("You are simple.");
    }

    private static AgentGenerator CreateGenerator()
    {
        var logger = new Mock<ILogger<AgentGenerator>>().Object;
        var engine = new AgentTemplateEngine();
        var validator = new AgentValidator();
        return new AgentGenerator(logger, engine, validator);
    }
}
