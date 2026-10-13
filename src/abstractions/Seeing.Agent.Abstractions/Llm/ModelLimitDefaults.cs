namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型上下文的默认限制值：当模型未声明（或声明为旧默认哨兵值）时回落到此。
/// </summary>
public static class ModelLimitDefaults
{
    /// <summary>默认上下文窗口（200k）。</summary>
    public const int Context = 200000;

    /// <summary>默认最大输出（64k）。</summary>
    public const int Output = 65536;
}
