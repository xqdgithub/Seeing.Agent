namespace Seeing.Agent.Abstractions.Skills;

/// <summary>发现期贡献描述（无正文）。</summary>
/// <param name="Key">目录名 / 命令 token（如 <c>mcp_server_skill</c>）。</param>
/// <param name="Description">短描述。</param>
/// <param name="ProviderId">提供方标识（如 <c>mcp:serverId</c>）。</param>
public sealed record SkillContributionDescriptor(
    string Key,
    string Description,
    string ProviderId);

/// <summary>加载期回源内容。</summary>
/// <param name="MarkdownBody">技能 Markdown 正文。</param>
/// <param name="Attachments">清单内附件（URI，不走磁盘目录）。</param>
public sealed record SkillContributionContent(
    string MarkdownBody,
    IReadOnlyList<SkillContributionAttachment> Attachments);

/// <summary>贡献技能附件。</summary>
/// <param name="RelativePath">清单相对路径。</param>
/// <param name="Uri">可读 URI（如 <c>skill://…</c> / resource URI）。</param>
/// <param name="MimeType">可选 MIME。</param>
public sealed record SkillContributionAttachment(
    string RelativePath,
    string Uri,
    string? MimeType);
