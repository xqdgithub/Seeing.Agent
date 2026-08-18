using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm;

/// <summary>
/// 预定义的模型配置
/// </summary>
public static class PredefinedModels
{
    /// <summary>OpenAI 模型</summary>
    public static readonly Dictionary<string, ModelConfig> OpenAI = new()
    {
        ["gpt-4o"] = new()
        {
            Id = "gpt-4o",
            Name = "GPT-4o",
            Provider = "openai",
            Modalities = new ModelModalities
            {
                Input = ["text", "image", "audio"],
                Output = ["text", "audio"]
            },
            Limit = new ModelLimits { Context = 128000, Output = 16384 },
            Pricing = new ModelPricing { Input = 2.5, Output = 10 }
        },
        ["gpt-4o-mini"] = new()
        {
            Id = "gpt-4o-mini",
            Name = "GPT-4o Mini",
            Provider = "openai",
            Modalities = new ModelModalities
            {
                Input = ["text", "image", "audio"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 128000, Output = 16384 },
            Pricing = new ModelPricing { Input = 0.15, Output = 0.6 }
        },
        ["gpt-4-turbo"] = new()
        {
            Id = "gpt-4-turbo",
            Name = "GPT-4 Turbo",
            Provider = "openai",
            Modalities = new ModelModalities
            {
                Input = ["text", "image"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 128000, Output = 4096 },
            Pricing = new ModelPricing { Input = 10, Output = 30 }
        },
        ["o1"] = new()
        {
            Id = "o1",
            Name = "o1",
            Provider = "openai",
            Modalities = new ModelModalities
            {
                Input = ["text"],
                Output = ["text"]
            },
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions { Type = "enabled", BudgetTokens = 8192 }
            },
            Limit = new ModelLimits { Context = 200000, Output = 100000 },
            Pricing = new ModelPricing { Input = 15, Output = 60 }
        }
    };

    /// <summary>Anthropic 模型</summary>
    public static readonly Dictionary<string, ModelConfig> Anthropic = new()
    {
        ["claude-sonnet-4-20250514"] = new()
        {
            Id = "claude-sonnet-4-20250514",
            Name = "Claude Sonnet 4",
            Provider = "anthropic",
            Modalities = new ModelModalities
            {
                Input = ["text", "image"],
                Output = ["text"]
            },
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions { Type = "enabled", BudgetTokens = 8192 }
            },
            Limit = new ModelLimits { Context = 200000, Output = 16000 },
            Pricing = new ModelPricing { Input = 3, Output = 15 }
        },
        ["claude-3-5-sonnet-20241022"] = new()
        {
            Id = "claude-3-5-sonnet-20241022",
            Name = "Claude 3.5 Sonnet",
            Provider = "anthropic",
            Modalities = new ModelModalities
            {
                Input = ["text", "image"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 200000, Output = 8192 },
            Pricing = new ModelPricing { Input = 3, Output = 15 }
        },
        ["claude-3-5-haiku-20241022"] = new()
        {
            Id = "claude-3-5-haiku-20241022",
            Name = "Claude 3.5 Haiku",
            Provider = "anthropic",
            Modalities = new ModelModalities
            {
                Input = ["text"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 200000, Output = 8192 },
            Pricing = new ModelPricing { Input = 0.8, Output = 4 }
        },
        ["claude-3-opus-20240229"] = new()
        {
            Id = "claude-3-opus-20240229",
            Name = "Claude 3 Opus",
            Provider = "anthropic",
            Modalities = new ModelModalities
            {
                Input = ["text", "image"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 200000, Output = 4096 },
            Pricing = new ModelPricing { Input = 15, Output = 75 }
        }
    };

    /// <summary>获取所有预定义模型（键为 provider/modelId）</summary>
    public static Dictionary<string, ModelConfig> GetAll()
    {
        var result = new Dictionary<string, ModelConfig>();
        foreach (var (key, value) in OpenAI)
            result[$"openai/{key}"] = value;
        foreach (var (key, value) in Anthropic)
            result[$"anthropic/{key}"] = value;
        return result;
    }
}

/// <summary>
/// 预定义的 Provider 配置
/// </summary>
public static class PredefinedProviders
{
    /// <summary>OpenAI Provider</summary>
    public static ProviderConfig OpenAI(string? apiKey = null) => new()
    {
        Id = "openai",
        Type = ProviderType.OpenAI,
        Name = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = apiKey,
        DefaultModel = "gpt-4o",
        Models = PredefinedModels.OpenAI
    };

    /// <summary>Anthropic Provider</summary>
    public static ProviderConfig Anthropic(string? apiKey = null) => new()
    {
        Id = "anthropic",
        Type = ProviderType.Anthropic,
        Name = "Anthropic",
        BaseUrl = "https://api.anthropic.com/v1",
        ApiKey = apiKey,
        DefaultModel = "claude-sonnet-4-20250514",
        Models = PredefinedModels.Anthropic
    };
}
