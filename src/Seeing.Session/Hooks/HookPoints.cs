namespace Seeing.Session.Hooks;

/// <summary>
/// Session Hook 点常量定义
/// </summary>
public static class HookPoints
{
    /// <summary>Session 创建 - 非阻塞</summary>
    public const string Created = "session.created";

    /// <summary>Session 更新 - 非阻塞</summary>
    public const string Updated = "session.updated";

    /// <summary>Session 销毁 - 非阻塞</summary>
    public const string Destroyed = "session.destroyed";

    /// <summary>Session 保存前 - 非阻塞</summary>
    public const string Saving = "session.saving";

    /// <summary>Session 保存后 - 非阻塞</summary>
    public const string Saved = "session.saved";

    /// <summary>Session 加载前 - 非阻塞</summary>
    public const string Loading = "session.loading";

    /// <summary>Session 加载后 - 非阻塞</summary>
    public const string Loaded = "session.loaded";

    /// <summary>Session 压缩后 - 非阻塞</summary>
    public const string Compressed = "session.compressed";

    /// <summary>Session 模型变更 - 非阻塞</summary>
    public const string ModelChanged = "session.model_changed";

    /// <summary>Session 分叉 - 非阻塞</summary>
    public const string SessionForked = "session.forked";

    /// <summary>Session 归档 - 非阻塞</summary>
    public const string SessionArchived = "session.archived";

    /// <summary>Session 分享 - 非阻塞</summary>
    public const string SessionShared = "session.shared";

    /// <summary>Session 回滚 - 非阻塞</summary>
    public const string SessionReverted = "session.reverted";
}