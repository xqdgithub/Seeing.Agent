using System.Text;

namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 子进程启动规格。
/// </summary>
public class SubprocessSpec
{
    /// <summary>可执行文件路径或名称（必填）。</summary>
    public required string FileName { get; set; }

    /// <summary>命令行参数。</summary>
    public string Arguments { get; set; } = "";

    /// <summary>工作目录；为 <see langword="null"/> 时使用当前目录。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>额外环境变量；未指定键继承父进程。</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; set; } =
        new Dictionary<string, string?>();

    /// <summary>是否重定向标准输出。</summary>
    public bool RedirectStandardOutput { get; set; } = true;

    /// <summary>是否重定向标准错误。</summary>
    public bool RedirectStandardError { get; set; } = true;

    /// <summary>标准流编码。</summary>
    public Encoding Encoding { get; set; } = Encoding.UTF8;

    /// <summary>执行超时；为 <see langword="null"/> 时不限制。</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>取消令牌。</summary>
    public CancellationToken CancellationToken { get; set; } = default;
}
