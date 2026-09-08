using Seeing.Agent.Abstractions.Execution;

namespace Seeing.IO.Local;

/// <summary>
/// 本地执行世界：当前工作目录 + 本地文件系统 + 本地子进程工厂。
/// </summary>
public sealed class LocalExecutionWorld : IExecutionWorld
{
    private readonly LocalFileSystem _fileSystem = new();
    private readonly LocalSubprocessFactory _subprocessFactory = new();

    /// <inheritdoc />
    public string Cwd => Directory.GetCurrentDirectory();

    /// <inheritdoc />
    public IFileSystem FileSystem => _fileSystem;

    /// <inheritdoc />
    public ISubprocessFactory Subprocess => _subprocessFactory;
}
