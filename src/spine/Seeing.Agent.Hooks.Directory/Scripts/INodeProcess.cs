namespace Seeing.Agent.Hooks.Directory.Scripts;

public interface INodeProcess : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    void Kill();
}

public interface INodeRuntime
{
    bool Probe();

    INodeProcess StartWorker(string workerPath);
}
