namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具元数据出口 - 向工具调用元数据存储写入键值
/// </summary>
public interface IToolMetadataSink
{
    /// <summary>设置元数据</summary>
    void SetMetadata(string key, Dictionary<string, object>? value);
}