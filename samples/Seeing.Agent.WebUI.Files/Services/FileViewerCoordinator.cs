using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>订阅磁盘变更流，驱动 FileTabsState 刷新/冲突。</summary>
public sealed class FileViewerCoordinator : IDisposable
{
    private readonly IFileWorkspace _workspace;
    private readonly FileTabsState _tabs;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>注入工作区与标签状态。</summary>
    public FileViewerCoordinator(IFileWorkspace workspace, FileTabsState tabs)
    {
        _workspace = workspace; _tabs = tabs;
    }

    /// <summary>启动订阅（回调在后台线程；调用方负责编组回 UI）。</summary>
    public void Start(Func<FileChangeEvent, Task> onEvent)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _workspace.SubscribeChangesAsync(_cts.Token).ConfigureAwait(false))
                    await onEvent(evt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>应用一个变更事件到标签状态（纯逻辑，可在测试中直接调用）。</summary>
    public async Task<ApplyOutcome> ApplyAsync(FileChangeEvent evt)
    {
        if (evt.Kind == FileChangeKind.Renamed && evt.OldPath is { } oldPath)
        {
            var newId = evt.Id;
            var oldId = _workspace.GetResourceId(oldPath);
            _tabs.ApplyRename(oldId, newId);
            return ApplyOutcome.Renamed;
        }
        if (evt.Kind == FileChangeKind.Deleted)
        {
            _tabs.MarkDeleted(evt.Id);
            return ApplyOutcome.Deleted;
        }
        var file = await _workspace.ReadAsync(_workspace.GetAbsolutePath(evt.Id), FileReadMode.Display).ConfigureAwait(false);
        var applied = _tabs.ApplyDiskUpdate(file);
        return applied ? ApplyOutcome.Refreshed : ApplyOutcome.ConflictSkipped;
    }

    /// <inheritdoc />
    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}

/// <summary>变更应用结果。</summary>
public enum ApplyOutcome { Refreshed, ConflictSkipped, Deleted, Renamed }
