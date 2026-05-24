using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Generation
{
    /// <summary>
    /// Agent 动态生成器实现
    /// </summary>
    public class AgentGenerator : IAgentGenerator
    {
        private readonly ILogger<AgentGenerator> _logger;
        private readonly AgentTemplateEngine _templateEngine;
        private readonly AgentValidator _validator;
        private readonly IAgentRegistry? _agentRegistry;
        private readonly ConcurrentDictionary<string, AgentTemplate> _templates = new();
        private readonly ConcurrentDictionary<string, AgentDefinition> _definitions = new();

        public AgentGenerator(
            ILogger<AgentGenerator> logger,
            AgentTemplateEngine templateEngine,
            AgentValidator validator,
            IAgentRegistry? agentRegistry = null)
        {
            _logger = logger;
            _templateEngine = templateEngine;
            _validator = validator;
            _agentRegistry = agentRegistry;
            LoadBuiltinTemplates();
        }

        public async Task<AgentDefinition> GenerateAsync(AgentGenerationRequest request, CancellationToken cancellationToken = default)
        {
            // 获取模板
            AgentTemplate? template = null;
            if (!string.IsNullOrEmpty(request.TemplateId))
            {
                template = _templates.GetValueOrDefault(request.TemplateId);
                if (template == null)
                    throw new InvalidOperationException($"Template not found: {request.TemplateId}");
            }

            // 渲染系统提示词
            string systemPrompt;
            if (template != null)
            {
                var renderResult = _templateEngine.Render(
                    template.SystemPromptTemplate,
                    request.Variables,
                    template.RequiredVariables);

                if (renderResult.HasErrors)
                    throw new InvalidOperationException($"Template render errors: {string.Join("; ", renderResult.Errors)}");

                systemPrompt = renderResult.RenderedContent;

                if (renderResult.Warnings.Any())
                {
                    foreach (var w in renderResult.Warnings)
                        _logger.LogWarning("Template render warning: {Warning}", w);
                }
            }
            else
            {
                systemPrompt = request.SystemPrompt;
            }

            // 构建 Agent 定义
            var definition = new AgentDefinition
            {
                Name = request.Name,
                Description = request.Description,
                SystemPrompt = systemPrompt,
                AllowedTools = MergeLists(template?.DefaultAllowedTools, request.AllowedTools),
                DeniedTools = MergeLists(template?.DefaultDeniedTools, request.DeniedTools),
                ModelConfig = request.ModelOverride ?? template?.DefaultModelOverride,
                MaxIterations = request.MaxIterations ?? template?.DefaultMaxIterations ?? 10,
                TimeoutSeconds = request.TimeoutSeconds ?? template?.DefaultTimeoutSeconds ?? 300,
                SourceTemplateId = template?.Id,
                Tags = MergeLists(template?.Tags, request.Tags)
            };

            // 验证
            var validationResult = _validator.Validate(definition);
            if (!validationResult.IsValid)
                throw new InvalidOperationException($"Agent validation failed: {string.Join("; ", validationResult.Errors)}");

            _definitions[definition.Id] = definition;

            // Register with agent registry if available
            if (_agentRegistry != null)
            {
                var agentInfo = ToAgentInfo(definition);
                await _agentRegistry.RegisterAgentAsync(agentInfo);
            }

            _logger.LogInformation("Generated agent {AgentName} from template {TemplateId}",
                definition.Name, template?.Id ?? "none");

            return definition;
        }

        public Task<AgentValidationResult> ValidateAsync(AgentDefinition definition, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_validator.Validate(definition));
        }

        public Task<IReadOnlyList<AgentTemplate>> ListTemplatesAsync(CancellationToken cancellationToken = default)
        {
            var templates = _templates.Values
                .OrderByDescending(t => t.IsBuiltin)
                .ThenBy(t => t.Name)
                .ToList();

            return Task.FromResult<IReadOnlyList<AgentTemplate>>(templates);
        }

        public Task<AgentTemplate?> GetTemplateAsync(string templateId, CancellationToken cancellationToken = default)
        {
            _templates.TryGetValue(templateId, out var template);
            return Task.FromResult(template);
        }

        public Task RegisterTemplateAsync(AgentTemplate template, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(template.Name))
                throw new ArgumentException("Template name is required");

            // 验证模板语法
            var validation = _templateEngine.ValidateTemplate(template.SystemPromptTemplate);
            if (!validation.IsValid)
                throw new InvalidOperationException($"Template syntax errors: {string.Join("; ", validation.Errors)}");

            template.UpdatedAt = DateTimeOffset.UtcNow;
            _templates[template.Id] = template;

            _logger.LogInformation("Registered template {TemplateId} ({Name})", template.Id, template.Name);
            return Task.CompletedTask;
        }

        public Task<AgentTemplate> ExtractTemplateAsync(AgentDefinition definition, string templateName, CancellationToken cancellationToken = default)
        {
            var template = new AgentTemplate
            {
                Name = templateName,
                Description = $"Template extracted from agent '{definition.Name}'",
                SystemPromptTemplate = definition.SystemPrompt,
                DefaultAllowedTools = new List<string>(definition.AllowedTools),
                DefaultDeniedTools = new List<string>(definition.DeniedTools),
                DefaultModelOverride = definition.ModelConfig,
                DefaultMaxIterations = definition.MaxIterations,
                DefaultTimeoutSeconds = definition.TimeoutSeconds,
                Tags = new List<string>(definition.Tags),
                IsBuiltin = false
            };

            _templates[template.Id] = template;
            _logger.LogInformation("Extracted template {TemplateId} from agent {AgentId}", template.Id, definition.Id);

            return Task.FromResult(template);
        }

        private void LoadBuiltinTemplates()
        {
            // 通用助手模板
            RegisterBuiltinTemplate(new AgentTemplate
            {
                Name = "general-assistant",
                Description = "General purpose assistant",
                Category = "general",
                SystemPromptTemplate = @"You are a helpful AI assistant named {{Name}}. 
{{Description}}

You have access to various tools and should use them when appropriate.
Always think step by step before acting.",
                DefaultMaxIterations = 10,
                DefaultTimeoutSeconds = 300,
                IsBuiltin = true
            });

            // 代码专家模板
            RegisterBuiltinTemplate(new AgentTemplate
            {
                Name = "code-expert",
                Description = "Software development expert agent",
                Category = "development",
                SystemPromptTemplate = @"You are an expert software developer named {{Name}}.
{{Description}}

Guidelines:
- Write clean, maintainable code
- Follow best practices and design patterns
- Add appropriate error handling
- Write tests when applicable
- Document public APIs",
                DefaultAllowedTools = new List<string> { "read", "write", "edit", "grep", "glob", "bash" },
                DefaultMaxIterations = 20,
                DefaultTimeoutSeconds = 600,
                IsBuiltin = true
            });

            // 研究员模板
            RegisterBuiltinTemplate(new AgentTemplate
            {
                Name = "researcher",
                Description = "Research and analysis agent",
                Category = "research",
                SystemPromptTemplate = @"You are a research specialist named {{Name}}.
{{Description}}

Approach:
- Gather information thoroughly before drawing conclusions
- Cross-reference multiple sources
- Present findings in a structured format
- Highlight uncertainties and assumptions",
                DefaultAllowedTools = new List<string> { "read", "grep", "glob", "webfetch", "websearch" },
                DefaultDeniedTools = new List<string> { "write", "edit", "bash" },
                DefaultMaxIterations = 15,
                DefaultTimeoutSeconds = 600,
                IsBuiltin = true
            });

            // 审查员模板
            RegisterBuiltinTemplate(new AgentTemplate
            {
                Name = "reviewer",
                Description = "Code review and quality assurance agent",
                Category = "quality",
                SystemPromptTemplate = @"You are a code reviewer named {{Name}}.
{{Description}}

Review criteria:
- Correctness and logic errors
- Security vulnerabilities
- Performance issues
- Code style and readability
- Test coverage",
                DefaultAllowedTools = new List<string> { "read", "grep", "glob" },
                DefaultDeniedTools = new List<string> { "write", "edit" },
                DefaultMaxIterations = 5,
                DefaultTimeoutSeconds = 300,
                IsBuiltin = true
            });
        }

        private void RegisterBuiltinTemplate(AgentTemplate template)
        {
            _templates[template.Id] = template;
        }

        private static List<string> MergeLists(List<string>? baseList, List<string> overrideList)
        {
            if (baseList == null || baseList.Count == 0) return new List<string>(overrideList);
            if (overrideList.Count == 0) return new List<string>(baseList);

            // 合并去重
            return baseList.Concat(overrideList).Distinct().ToList();
        }

        /// <summary>
        /// Converts an AgentDefinition to AgentInfo for registry registration
        /// </summary>
        private static AgentInfo ToAgentInfo(AgentDefinition definition)
        {
            // Convert ModelConfigOverride to ModelReference
            ModelReference? modelRef = null;
            if (definition.ModelConfig is { } mc && !string.IsNullOrEmpty(mc.ModelId))
            {
                modelRef = new ModelReference
                {
                    ProviderId = mc.Provider ?? string.Empty,
                    ModelId = mc.ModelId
                };
            }

            return new AgentInfo
            {
                Name = definition.Name,
                Description = definition.Description ?? string.Empty,
                SystemPrompt = definition.SystemPrompt,
                Model = modelRef,
                MaxSteps = definition.MaxIterations,
                AllowedTools = definition.AllowedTools.ToList(),
                DeniedTools = definition.DeniedTools.ToList(),
                Tags = definition.Tags.ToList()
            };
        }
    }
}
