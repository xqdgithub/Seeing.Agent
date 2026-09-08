namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 执行世界 — 文件系统与子进程工厂成对绑定，由 <c>seams.executionWorld</c> 一次替换。
/// </summary>
public interface IExecutionWorld
{
    /// <summary>当前工作目录。</summary>
    string Cwd { get; }

    /// <summary>绑定的文件系统。</summary>
    IFileSystem FileSystem { get; }

    /// <summary>绑定的子进程工厂。</summary>
    ISubprocessFactory Subprocess { get; }
}
