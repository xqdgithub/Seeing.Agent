namespace Seeing.Agent.Abstractions.Agents;

/// <summary>
/// 模型引用 - 指向特定 Provider 的模型
/// </summary>
public class ModelReference
{
    /// <summary>提供商 ID（如 openai、anthropic）</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>模型 ID（如 gpt-4o、claude-3-5-sonnet）</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// 转换为字符串表示（provider/model 格式）
    /// </summary>
    public override string ToString()
    {
        return string.IsNullOrEmpty(ProviderId)
            ? ModelId
            : $"{ProviderId}/{ModelId}";
    }

    /// <summary>
    /// 从字符串解析模型引用
    /// </summary>
    public static ModelReference? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var parts = value.Split(new[] { ':', '/' }, 2);
        if (parts.Length >= 2)
        {
            return new ModelReference
            {
                ProviderId = parts[0],
                ModelId = parts[1]
            };
        }

        // 只有模型 ID，无 Provider
        return new ModelReference
        {
            ProviderId = string.Empty,
            ModelId = parts[0]
        };
    }
}