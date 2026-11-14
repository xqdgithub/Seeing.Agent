namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 手动「生成 SKILL」时用户在弹窗里确认/改过的草稿。
/// <para>
/// 与建议清单里的原始字段分开：journal 保留模型原话（含中文 target）供审计；
/// 落盘用这份用户确认过的草稿，避免「一键直写不合规名」。
/// </para>
/// </summary>
/// <param name="Name">技能名（须满足 <see cref="ReflectionSuggestionsWriter.TryValidateSkillName"/>）。</param>
/// <param name="Description">技能选型描述（来自 suggestion.Description，可改；勿用备注）。</param>
/// <param name="Content">正文（来自 suggestedContent，可改）。</param>
public sealed record ReflectionSkillDraft(string Name, string Description, string Content);
