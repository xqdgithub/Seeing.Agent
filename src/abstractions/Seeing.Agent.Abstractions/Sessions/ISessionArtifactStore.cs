namespace Seeing.Agent.Abstractions.Sessions;

/// <summary>
/// 会话资源目录端口：把工具产出的二进制（截图、导出文件等）写进**本会话的资源目录**，
/// 返回可被图片物化器接受的本地路径。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这条端口</b>：图片在会话里只存 <c>LocalPath</c>（避免会话文件因 base64 写放大），
/// 而物化器只信任<b>本会话资源目录</b>内的路径。该目录的解析依赖会话存储（会随工作区切换重定位），
/// 住在脊柱层；能力包只依赖 Abstractions，因此必须有一条契约端口。
/// </para>
/// <para>
/// <b>实现方职责</b>：返回的路径必须落在该会话自己的资源目录内。
/// </para>
/// <para>
/// <b>契约不承诺</b>「同名多次调用不覆盖」——那是实现的附加行为（见各实现注释）。
/// 契约只保证「写入成功且路径在本会话资源目录内」。
/// </para>
/// </remarks>
public interface ISessionArtifactStore
{
    /// <summary>
    /// 写入二进制产物并返回其绝对路径。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="name">
    /// 逻辑名（用于生成文件名，如 <c>browser-shot</c>）。
    /// 同一次会话内多次调用应各自得到不同文件（实现负责）；调用方不应依赖具体文件名。
    /// </param>
    /// <param name="content">内容。</param>
    /// <param name="extension">扩展名，如 <c>.png</c>；不合法时回退 <c>.bin</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>产物绝对路径（在本会话资源目录内）。</returns>
    Task<string> SaveAsync(
        string sessionId,
        string name,
        ReadOnlyMemory<byte> content,
        string extension,
        CancellationToken cancellationToken = default);
}
