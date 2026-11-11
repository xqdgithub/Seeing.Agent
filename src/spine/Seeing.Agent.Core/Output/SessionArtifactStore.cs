using Seeing.Agent.Abstractions.Sessions;

namespace Seeing.Agent.Core.Output;

/// <summary>
/// <see cref="ISessionArtifactStore"/> 的唯一实现：把产物写进会话资源目录。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么落会话资源目录而不是别处</b>：<c>ImagePartResolver</c> 的路径边界校验以该目录为根，
/// 写别处的图片会被判越界并降级成文本占位——工具截图就永远送不进模型。
/// </para>
/// <para>
/// <b>同名不覆盖</b>（本类承诺的行为，契约只保证「落在本会话资源目录内」）：
/// 唯一性由 <c>name</c> 后缀的单调递增序号保证。放 <c>name</c> 而不是底层 store 的 <c>suffix</c>，
/// 是因为那个 suffix 会被截断到 8 个 ASCII 字符，把时间戳截断后恰好丢掉最低位 →
/// 同一毫秒内两次写入撞名，后写覆盖先写（截图静默丢失）。
/// </para>
/// <para>
/// <b>跨进程不安全</b>：序号起点是进程内的 <c>static</c> 字段。两个宿主进程写同一会话目录时理论上可能撞名；
/// 实践中同一会话只由一个宿主持有，且撞名后果是「两张图其中一个被覆盖」，属已记录的边界。
/// </para>
/// </remarks>
public sealed class SessionArtifactStore : ISessionArtifactStore
{
    private static long s_lastStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private readonly IToolOutputStore _outputStore;

    /// <summary>创建存储。</summary>
    /// <param name="outputStore">工具输出落盘服务（ref 目录唯一解析点）。</param>
    public SessionArtifactStore(IToolOutputStore outputStore)
    {
        _outputStore = outputStore ?? throw new ArgumentNullException(nameof(outputStore));
    }

    /// <inheritdoc />
    public Task<string> SaveAsync(
        string sessionId,
        string name,
        ReadOnlyMemory<byte> content,
        string extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // 唯一性放在 name 里，而不是 suffix：
        // IToolOutputStore 会把 suffix 截到 8 个 ASCII 字符（SessionToolOutputStore.SanitizeExtension），
        // 而纳秒级时间戳的 base36 有 8 位以上 → 截断恰好砍掉最低位，同一毫秒内两次写入撞名，
        // 后写覆盖先写（截图静默丢失）。name 只做非法字符替换、不截断，是安全的承载位置。
        var uniqueName = name + "-" + ToBase36(NextStamp());

        return _outputStore.SaveBytesAsync(
            sessionId,
            uniqueName,
            content,
            extension,
            suffix: null,
            cancellationToken);
    }

    /// <summary>把递增序号编成 base36（小写），保证长度 ≤ 8。</summary>
    private static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";

        if (value <= 0)
            return "0";

        Span<char> buffer = stackalloc char[13];
        var index = buffer.Length;

        while (value > 0)
        {
            buffer[--index] = digits[(int)(value % 36)];
            value /= 36;
        }

        return new string(buffer[index..]);
    }

    /// <summary>取下一个严格递增的序号（毫秒起点 + 同毫秒内自增），并发安全。</summary>
    private static long NextStamp()
    {
        while (true)
        {
            var current = Interlocked.Read(ref s_lastStamp);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var next = now > current ? now : current + 1;

            if (Interlocked.CompareExchange(ref s_lastStamp, next, current) == current)
                return next;
        }
    }
}
