namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// 记忆类型
/// </summary>
public enum MemoryType
{
    /// <summary>原始会话记录</summary>
    Session,
    
    /// <summary>每日浅加工记忆</summary>
    Daily,
    
    /// <summary>长期记忆 (LLM 整合)</summary>
    Digest
}
