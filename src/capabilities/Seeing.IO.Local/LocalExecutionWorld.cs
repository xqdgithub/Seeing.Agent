using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.IO.Local;

/// <summary>
/// 本地执行世界：当前工作目录 + 本地文件系统 + 本地子进程工厂。
/// </summary>
public sealed class LocalExecutionWorld : IExecutionWorld
{
    private readonly LocalFileSystem _fileSystem = new();
    private readonly LocalSubprocessFactory _subprocessFactory;

    /// <summary>创建本地执行世界。</summary>
    /// <param name="loggerFactory">可选日志工厂；子进程生命周期绑定失败时只记日志并降级。</param>
    public LocalExecutionWorld(ILoggerFactory? loggerFactory = null)
    {
        _subprocessFactory = new LocalSubprocessFactory(
            loggerFactory?.CreateLogger("Seeing.IO.Local.LocalSubprocessFactory"));
    }

    /// <inheritdoc />
    public string Cwd => Directory.GetCurrentDirectory();

    /// <inheritdoc />
    public IFileSystem FileSystem => _fileSystem;

    /// <inheritdoc />
    public ISubprocessFactory Subprocess => _subprocessFactory;
}
