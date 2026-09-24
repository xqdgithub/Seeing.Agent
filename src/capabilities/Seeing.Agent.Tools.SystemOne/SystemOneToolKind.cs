namespace Seeing.Agent.Core.Tools.SystemOne;

/// <summary>systemone 工具种类（Ask=混合，其余为同类）。</summary>
public enum SystemOneToolKind
{
    /// <summary>混合：questions 每项带 type，支持多题型合并到一次请求。</summary>
    Ask,

    /// <summary>同类：是/否概率。</summary>
    Noul,

    /// <summary>同类：多选一。</summary>
    Choice,

    /// <summary>同类：有序分级打分。</summary>
    Score
}
