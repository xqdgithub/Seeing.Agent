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
                {
                    try
                    {
                        await onEvent(evt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        // 单个事件处理失败（如删除竞态）不应终止整条订阅循环
                    }
                }
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

        // watcher 事件与实际磁盘状态存在竞态（文件在事件到达前被删除/重命名）；
        // 读取失败视为已删除，避免异常冒泡到渲染器。
        FileReadResult file;
        try
        {
            file = await _workspace.ReadAsync(_workspace.GetAbsolutePath(evt.Id), FileReadMode.Display).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            _tabs.MarkDeleted(evt.Id);
            return ApplyOutcome.Deleted;
        }
        catch (DirectoryNotFoundException)
        {
            _tabs.MarkDeleted(evt.Id);
            return ApplyOutcome.Deleted;
        }
        catch (IOException)
        {
            // 半写/占用等瞬时错误：跳过本次刷新，保留现有标签内容
            return ApplyOutcome.ConflictSkipped;
        }

        var binary = await ReadBinaryIfNeededAsync(file).ConfigureAwait(false);
        var applied = _tabs.ApplyDiskUpdate(file, binary);
        if (!applied)
        {
            // 本地有未保存改动：不覆盖，标记冲突等待用户裁决
            _tabs.MarkExternalConflict(evt.Id);
            return ApplyOutcome.ConflictSkipped;
        }
        return ApplyOutcome.Refreshed;
    }

    private async Task<byte[]?> ReadBinaryIfNeededAsync(FileReadResult file)
    {
        if (file.Kind is not (FileContentKind.Image or FileContentKind.Binary))
            return null;
        try
        {
            return await _workspace.ReadBytesAsync(_workspace.GetAbsolutePath(file.Id)).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}

/// <summary>变更应用结果。</summary>
public enum ApplyOutcome { Refreshed, ConflictSkipped, Deleted, Renamed }
