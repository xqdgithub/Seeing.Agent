using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>单个打开文件标签。</summary>
public sealed class OpenFile
{
    /// <summary>资源标识。</summary>
    public required FileResourceId Id { get; set; }
    /// <summary>当前编辑器内容（未保存时与磁盘不同）。</summary>
    public required string Content { get; set; }
    /// <summary>内容类型（决定查看器渲染方式）。</summary>
    public FileContentKind Kind { get; set; } = FileContentKind.Text;
    /// <summary>MIME 类型。</summary>
    public string MimeType { get; set; } = "application/octet-stream";
    /// <summary>二进制/图片的原始字节（Content 为空时用于渲染）。</summary>
    public byte[]? BinaryContent { get; set; }
    /// <summary>打开时的磁盘修订号。</summary>
    public FileRevision? OpenedRevision { get; set; }
    /// <summary>是否有未保存修改。</summary>
    public bool HasUnsavedChanges { get; set; }
    /// <summary>是否只读（截断/非 UTF-8/超大）。</summary>
    public bool IsReadOnly { get; set; }
    /// <summary>是否已从磁盘删除。</summary>
    public bool IsDeleted { get; set; }
    /// <summary>最近一次 agent/磁盘 diff（可选）。</summary>
    public string? PendingDiff { get; set; }
    /// <summary>磁盘已被外部修改且本地有未保存改动（待用户裁决）。</summary>
    public bool HasExternalConflict { get; set; }
    /// <summary>磁盘内容版本号：仅在磁盘内容被应用时自增（用于编辑器重建判定，用户编辑不变）。</summary>
    public int DiskVersion { get; set; }
}

/// <summary>多标签状态（纯 C#，UI 无关）。</summary>
public sealed class FileTabsState
{
    private readonly List<OpenFile> _open = [];
    private readonly Dictionary<string, OpenFile> _byKey = new(StringComparer.Ordinal);

    /// <summary>标签集合变更事件。</summary>
    public event Action? Changed;

    /// <summary>已打开标签。</summary>
    public IReadOnlyList<OpenFile> OpenFiles => _open;

    /// <summary>当前激活标签键。</summary>
    public string? ActiveKey { get; private set; }

    private static string Key(FileResourceId id) => id.ToString();

    /// <summary>打开或激活文件。</summary>
    public void Open(FileReadResult file, byte[]? binaryContent = null)
    {
        var key = Key(file.Id);
        if (_byKey.TryGetValue(key, out var existing))
        {
            existing.Kind = file.Kind;
            existing.MimeType = file.MimeType;
            existing.BinaryContent = binaryContent;
            ActiveKey = key;
        }        else
        {
            var of = new OpenFile
            {
                Id = file.Id,
                Content = file.Content ?? "",
                Kind = file.Kind,
                MimeType = file.MimeType,
                BinaryContent = binaryContent,
                OpenedRevision = file.Revision,
                IsReadOnly = !file.IsEditable,
            };
            _open.Add(of);
            _byKey[key] = of;
            ActiveKey = key;
        }
        Changed?.Invoke();
    }

    /// <summary>关闭标签。</summary>
    public void Close(FileResourceId id)
    {
        var key = Key(id);
        if (!_byKey.Remove(key, out var of)) return;
        _open.Remove(of);
        if (ActiveKey == key) ActiveKey = _open.Count > 0 ? Key(_open[^1].Id) : null;
        Changed?.Invoke();
    }

    /// <summary>激活标签。</summary>
    public void Activate(FileResourceId id)
    {
        if (_byKey.ContainsKey(Key(id))) { ActiveKey = Key(id); Changed?.Invoke(); }
    }

    /// <summary>标记未保存修改。</summary>
    public void MarkDirty(FileResourceId id, string content)
    {
        if (!_byKey.TryGetValue(Key(id), out var of)) return;
        of.Content = content; of.HasUnsavedChanges = true;
        Changed?.Invoke();
    }

    /// <summary>应用磁盘更新（仅当无未保存修改时覆盖内容）。</summary>
    public bool ApplyDiskUpdate(FileReadResult file, byte[]? binaryContent = null)
    {
        if (!_byKey.TryGetValue(Key(file.Id), out var of)) return false;
        if (of.HasUnsavedChanges) return false;
        of.Content = file.Content ?? ""; of.OpenedRevision = file.Revision;
        of.Kind = file.Kind; of.MimeType = file.MimeType; of.BinaryContent = binaryContent;
        of.IsReadOnly = !file.IsEditable; of.IsDeleted = false;
        of.HasExternalConflict = false; of.PendingDiff = null;
        of.DiskVersion++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>标记外部冲突（磁盘变更但本地有未保存改动，等待用户裁决）。</summary>
    public void MarkExternalConflict(FileResourceId id)
    {
        if (_byKey.TryGetValue(Key(id), out var of)) { of.HasExternalConflict = true; Changed?.Invoke(); }
    }

    /// <summary>冲突裁决：重新加载（丢弃本地改动，采用磁盘内容）。</summary>
    public void ResolveConflictReload(FileResourceId id, FileReadResult file, byte[]? binaryContent = null)
    {
        if (!_byKey.TryGetValue(Key(id), out var of)) return;
        of.Content = file.Content ?? ""; of.OpenedRevision = file.Revision;
        of.Kind = file.Kind; of.MimeType = file.MimeType; of.BinaryContent = binaryContent;
        of.IsReadOnly = !file.IsEditable; of.IsDeleted = false;
        of.HasUnsavedChanges = false; of.HasExternalConflict = false; of.PendingDiff = null;
        of.DiskVersion++;
        Changed?.Invoke();
    }

    /// <summary>冲突裁决：保留本地改动（刷新基准 revision 以便下次保存）。</summary>
    public void ResolveConflictKeepLocal(FileResourceId id, FileRevision? currentDiskRevision)
    {
        if (!_byKey.TryGetValue(Key(id), out var of)) return;
        of.OpenedRevision = currentDiskRevision; of.HasExternalConflict = false;
        Changed?.Invoke();
    }

    /// <summary>应用重命名（含目录前缀重写）。</summary>
    public void ApplyRename(FileResourceId oldId, FileResourceId newId)
    {
        var prefix = oldId.RelativePath.TrimEnd('/') + "/";
        var toRename = _open.Where(o => o.Id.RelativePath == oldId.RelativePath
            || o.Id.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var of in toRename)
        {
            _byKey.Remove(Key(of.Id));
            var newRel = of.Id.RelativePath == oldId.RelativePath
                ? newId.RelativePath
                : newId.RelativePath + of.Id.RelativePath[oldId.RelativePath.Length..];
            of.Id = new FileResourceId(newId.RootId, newRel);
            _byKey[Key(of.Id)] = of;
        }
        Changed?.Invoke();
    }

    /// <summary>标记文件已删除。</summary>
    public void MarkDeleted(FileResourceId id)
    {
        if (_byKey.TryGetValue(Key(id), out var of)) { of.IsDeleted = true; Changed?.Invoke(); }
    }

    /// <summary>设置 agent diff（供视图渲染）。</summary>
    public void SetPendingDiff(FileResourceId id, string? diff)
    {
        if (_byKey.TryGetValue(Key(id), out var of)) { of.PendingDiff = diff; Changed?.Invoke(); }
    }
}
