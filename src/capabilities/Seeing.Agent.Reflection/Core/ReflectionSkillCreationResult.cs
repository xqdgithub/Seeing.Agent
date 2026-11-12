namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 手动「生成 SKILL」的结果。
/// <para>
/// 与 <see cref="ReflectionRunResult"/> 分开：那是「一次复盘跑得怎么样」，
/// 这是「一条建议能不能落成技能」。两者失败原因的价值不同——
/// 后者需要把具体原因（重名 / 非法字符 / 注入片段）直接展示给用户。
/// </para>
/// </summary>
/// <param name="Success">是否写入成功。</param>
/// <param name="SkillName">写入成功的技能名；失败时为 <c>null</c>。</param>
/// <param name="Error">失败原因；成功时为 <c>null</c>。</param>
public sealed record ReflectionSkillCreationResult(bool Success, string? SkillName, string? Error)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="skillName">写入成功的技能名。</param>
    public static ReflectionSkillCreationResult Created(string skillName)
        => new(true, skillName, null);

    /// <summary>构造失败结果。</summary>
    /// <param name="error">可直接展示的失败原因。</param>
    public static ReflectionSkillCreationResult Failed(string error)
        => new(false, null, error);
}
