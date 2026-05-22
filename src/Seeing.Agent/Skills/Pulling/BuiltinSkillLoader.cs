using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Skills.Pulling
{
    /// <summary>
    /// 内置 Skill 加载器 - 从程序集资源加载内置 Skills
    /// </summary>
    public class BuiltinSkillLoader
    {
        private readonly ILogger<BuiltinSkillLoader> _logger;
        private readonly string _skillStoragePath;
        private bool _loaded = false;

        public BuiltinSkillLoader(ILogger<BuiltinSkillLoader> logger, string? skillStoragePath = null)
        {
            _logger = logger;
            _skillStoragePath = skillStoragePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "skills", "builtin");
        }

        /// <summary>加载所有内置 Skills</summary>
        public async Task<IReadOnlyList<BuiltinSkill>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            if (_loaded)
            {
                return await ListLoadedAsync(cancellationToken);
            }

            var skills = new List<BuiltinSkill>();

            // 从程序集资源加载
            var assembly = Assembly.GetExecutingAssembly();
            var resourcePrefix = $"{assembly.GetName().Name}.Skills.Builtin.";

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(resourcePrefix) || !resourceName.EndsWith(".md"))
                    continue;

                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;

                    using var reader = new StreamReader(stream);
                    var content = await reader.ReadToEndAsync(cancellationToken);

                    var skillName = resourceName[(resourcePrefix.Length)..^3]; // 移除前缀和 .md
                    var skill = ParseBuiltinSkill(skillName, content);
                    skills.Add(skill);

                    // 保存到存储路径
                    await SaveBuiltinSkillAsync(skill, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load builtin skill: {Resource}", resourceName);
                }
            }

            // 如果没有资源，创建默认内置 Skills
            if (skills.Count == 0)
            {
                skills.AddRange(CreateDefaultBuiltinSkills());
                foreach (var skill in skills)
                {
                    await SaveBuiltinSkillAsync(skill, cancellationToken);
                }
            }

            _loaded = true;
            _logger.LogInformation("Loaded {Count} builtin skills", skills.Count);
            return skills;
        }

        /// <summary>列出已加载的内置 Skills</summary>
        public async Task<IReadOnlyList<BuiltinSkill>> ListLoadedAsync(CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_skillStoragePath))
                return new List<BuiltinSkill>();

            var skills = new List<BuiltinSkill>();
            foreach (var file in Directory.GetFiles(_skillStoragePath, "*.md"))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file, cancellationToken);
                    var skillName = Path.GetFileNameWithoutExtension(file);
                    skills.Add(ParseBuiltinSkill(skillName, content));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read builtin skill: {File}", file);
                }
            }

            return skills;
        }

        /// <summary>获取特定内置 Skill</summary>
        public async Task<BuiltinSkill?> GetAsync(string skillName, CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(_skillStoragePath, $"{skillName}.md");
            if (!File.Exists(path)) return null;

            try
            {
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                return ParseBuiltinSkill(skillName, content);
            }
            catch
            {
                return null;
            }
        }

        private BuiltinSkill ParseBuiltinSkill(string name, string content)
        {
            var lines = content.Split('\n');
            var description = "";
            var tags = new List<string>();

            // 解析 frontmatter
            if (lines.Length > 0 && lines[0].Trim() == "---")
            {
                var endIdx = Array.IndexOf(lines, "---", 1);
                if (endIdx > 0)
                {
                    for (int i = 1; i < endIdx; i++)
                    {
                        var line = lines[i];
                        if (line.StartsWith("description:"))
                            description = line["description:".Length..].Trim();
                        else if (line.StartsWith("tags:"))
                        {
                            var tagStr = line["tags:".Length..].Trim();
                            tags = tagStr.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                        }
                    }
                }
            }

            // 从第一个标题提取描述
            if (string.IsNullOrEmpty(description))
            {
                foreach (var line in lines)
                {
                    if (line.StartsWith("# "))
                    {
                        description = line[2..].Trim();
                        break;
                    }
                }
            }

            return new BuiltinSkill
            {
                Name = name,
                Description = description,
                Content = content,
                Tags = tags,
                Path = Path.Combine(_skillStoragePath, $"{name}.md")
            };
        }

        private async Task SaveBuiltinSkillAsync(BuiltinSkill skill, CancellationToken ct)
        {
            Directory.CreateDirectory(_skillStoragePath);
            var path = Path.Combine(_skillStoragePath, $"{skill.Name}.md");
            await File.WriteAllTextAsync(path, skill.Content, ct);
        }

        private IEnumerable<BuiltinSkill> CreateDefaultBuiltinSkills()
        {
            yield return new BuiltinSkill
            {
                Name = "code-review",
                Description = "Code review skill",
                Content = @"---
description: Perform comprehensive code review
tags: code, review, quality
---
# Code Review Skill

You are a code reviewer. Analyze the provided code and provide feedback on:

1. **Correctness** - Logic errors, edge cases
2. **Security** - Vulnerabilities, unsafe patterns
3. **Performance** - Inefficiencies, bottlenecks
4. **Readability** - Naming, structure, comments
5. **Best Practices** - Design patterns, idioms

Provide actionable suggestions with specific line references.",
                Tags = new List<string> { "code", "review", "quality" }
            };

            yield return new BuiltinSkill
            {
                Name = "test-generator",
                Description = "Generate unit tests",
                Content = @"---
description: Generate unit tests for code
tags: test, unit, tdd
---
# Test Generator Skill

You are a test generation specialist. For the provided code:

1. Identify all public methods and their behaviors
2. Generate comprehensive unit tests covering:
   - Happy path scenarios
   - Edge cases and boundary conditions
   - Error handling
   - Invalid inputs

Use the testing framework appropriate for the language (xUnit, NUnit, Jest, etc.).",
                Tags = new List<string> { "test", "unit", "tdd" }
            };

            yield return new BuiltinSkill
            {
                Name = "documentation",
                Description = "Generate documentation",
                Content = @"---
description: Generate documentation for code
tags: docs, api, markdown
---
# Documentation Skill

You are a documentation specialist. Generate clear documentation:

1. **API Documentation** - Method signatures, parameters, return values
2. **Usage Examples** - Code examples showing common use cases
3. **Remarks** - Important notes, caveats, best practices

Format output in Markdown with proper headings and code blocks.",
                Tags = new List<string> { "docs", "api", "markdown" }
            };
        }
    }

    /// <summary>内置 Skill 模型</summary>
    public class BuiltinSkill
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string Path { get; set; } = string.Empty;
    }
}
