namespace Seeing.Session.Hooks;

/// <summary>
/// Session Hook 点常量定义
/// </summary>
public static class HookPoints
{
    /// <summary>Session 创建 - 非阻塞</summary>
    public const string Created = "session.created";
    
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
}