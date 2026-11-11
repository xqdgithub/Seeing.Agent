namespace Seeing.Agent.Core.Output;

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

    /// <summary>
    /// 保存二进制内容（如图片截图）到会话 ref 目录，返回文件路径。
    /// <para>
    /// 文件名 = 净化后的 <paramref name="callId"/> + <paramref name="extension"/>（自动补点）。
    /// 与文本保存共用同一超时与原子写语义。
    /// </para>
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="callId">工具调用 id（用于生成唯一文件名）。</param>
    /// <param name="content">字节内容。</param>
    /// <param name="extension">扩展名，如 <c>.png</c>；不合法时回退 <c>.bin</c>。</param>
    /// <param name="suffix">可选文件名后缀（同一 callId 多张图时区分，如 <c>-1</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<string> SaveBytesAsync(
        string sessionId,
        string callId,
        ReadOnlyMemory<byte> content,
        string extension,
        string? suffix = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除会话 ref 目录（删除失败仅记录，不抛异常）</summary>
    void DeleteSessionRefDirectory(string sessionId);
}
