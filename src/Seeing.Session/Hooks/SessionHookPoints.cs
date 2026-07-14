namespace Seeing.Session.Hooks;

public static class SessionHookPoints
{
    public const string Created = "session.created";
    public const string Updated = "session.updated";
    public const string Saving = "session.saving";
    public const string Saved = "session.saved";
    public const string Loading = "session.loading";
    public const string Loaded = "session.loaded";
    public const string Destroyed = "session.destroyed";
    public const string Compressed = "session.compressed";
    
    /// <summary>
    /// 模型变更事件 - 当用户切换模型时触发
    /// </summary>
    public const string ModelChanged = "session.model_changed";
}