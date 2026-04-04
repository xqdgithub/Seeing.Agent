namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// Skill 信息 - 技能只是数据模型，提供上下文给 LLM
    /// </summary>
    public class SkillInfo
    {
        /// <summary>技能名称（必需，1-64字符，小写字母数字+连字符）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>技能描述（必需，1-1024 字符）</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>SKILL.md 文件路径</summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>技能 Markdown 内容（不含 frontmatter）</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>技能版本（可选）</summary>
        public string? Version { get; set; }

        /// <summary>技能作者（可选）</summary>
        public string? Author { get; set; }

        /// <summary>技能标签（可选）</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>技能依赖（可选）</summary>
        public List<string> Requires { get; set; } = new();

        /// <summary>许可证（可选）</summary>
        public string? License { get; set; }

        /// <summary>兼容性标记（可选）</summary>
        public string? Compatibility { get; set; }

        /// <summary>自定义元数据（可选）</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>技能目录路径（Location 的目录部分）</summary>
        public string DirectoryPath => Path.GetDirectoryName(Location) ?? string.Empty;
    }
}