namespace Seeing.Agent.Output;

/// <summary>
/// 工具输出存储 - 超长工具输出的落盘服务。
/// <para>完整内容写入会话 ref 目录（{会话存储目录}/{sessionId}.ref/），随会话保留、可关联、不丢失。</para>
/// </summary>
public interface IToolOutputStore
{
    /// <summary>解析会话 ref 目录（不创建；路径 = 会话存储目录/{sessionId}.ref）</summary>
    string GetRefDirectory(string sessionId);

    /// <summary>保存工具输出全文，返回文件路径（文件名 = 净化后的 callId.txt，5 秒内未完成抛 TimeoutException）</summary>
    Task<string> SaveAsync(string sessionId, string callId, string content, CancellationToken cancellationToken = default);

    /// <summary>删除会话 ref 目录（删除失败仅记录，不抛异常）</summary>
    void DeleteSessionRefDirectory(string sessionId);
}
