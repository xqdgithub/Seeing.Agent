using Acp.Interfaces;
using Acp.Messages;
using Acp.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Extensions;
using Seeing.Agent.Hooks;
using Seeing.Agent.Llm;
using Seeing.Agent.Rules;
using Seeing.Agent.Skills;
using System.Collections.Concurrent;
using System.Text.Json;
using IAcpAgent = Acp.Interfaces.IAgent;

namespace Seeing.Agent.Sample
{
    /// <summary>
    /// Seeing.Agent 使用示例 - 包含 ACP 协议支持
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Seeing.Agent 示例程序 ===\n");

            // 1. 配置服务容器
            var services = new ServiceCollection();
            
            // 注册 Seeing.Agent 服务
            services.AddSeeingAgent(options =>
            {
                options.DefaultModel = "gpt-4o";
                options.DefaultProvider = "openai";
                options.DefaultAgent = "primary";
                options.Skills.Paths.Add("./skills");
                options.Skills.Paths.Add("./.agents/skills");
                
                // 配置提供商
                options.Providers["openai"] = new ProviderConfig
                {
                    Id = "openai",
                    Type = ProviderType.OpenAI,
                    BaseUrl = "https://api.openai.com/v1",
                    Timeout = 60000,
                    MaxRetries = 3
                };
            });
            
            // 注册自定义 Hook 处理器
            services.AddSingleton<IHookHandler, LogHookHandler>();
            
            // 注册自定义 Tool
            services.AddSingleton<ITool, CalculatorTool>();
            
            // 注册 ACP Agent（示例）
            services.AddSingleton<IAcpAgent, SampleAcpAgent>();
            
            var serviceProvider = services.BuildServiceProvider();

            Console.WriteLine("服务已注册完成\n");

            // 2. 使用 RuleEngine 配置权限
            var ruleEngine = serviceProvider.GetRequiredService<RuleEngine>();
            
            ruleEngine.AddRule(new PermissionRule
            {
                Permission = "file_read",
                Pattern = "/public/*",
                Action = PermissionAction.Allow
            });
            
            ruleEngine.AddRule(new PermissionRule
            {
                Permission = "file_write",
                Pattern = "/system/*",
                Action = PermissionAction.Deny
            });
            
            ruleEngine.AddRule(new PermissionRule
            {
                Permission = "tool",
                Pattern = "dangerous_*",
                Action = PermissionAction.Ask
            });
            
            Console.WriteLine("权限规则已配置:");
            foreach (var rule in ruleEngine.GetRules())
            {
                Console.WriteLine($"  - {rule.Permission}: {rule.Pattern} => {rule.Action}");
            }
            Console.WriteLine();

            // 3. 测试权限评估
            Console.WriteLine("权限评估测试:");
            Console.WriteLine($"  file_read /public/test.txt => {ruleEngine.Evaluate("file_read", "/public/test.txt")}");
            Console.WriteLine($"  file_write /system/config => {ruleEngine.Evaluate("file_write", "/system/config")}");
            Console.WriteLine($"  file_delete /any/path => {ruleEngine.Evaluate("file_delete", "/any/path")}");
            Console.WriteLine($"  tool dangerous_cmd => {ruleEngine.Evaluate("tool", "dangerous_cmd")}");
            Console.WriteLine();

            // 4. 使用 SkillManager（技能现在是纯数据模型，通过 SkillTool 加载）
            var skillManager = serviceProvider.GetRequiredService<SkillManager>();
            
            // 发现技能
            await skillManager.DiscoverSkillsAsync();
            
            Console.WriteLine("已发现技能:");
            foreach (var info in skillManager.GetAllSkillInfos())
            {
                Console.WriteLine($"  - {info.Key}: {info.Value.Description}");
            }
            Console.WriteLine();

            // 5. 使用 HookManager
            var hookManager = serviceProvider.GetRequiredService<HookManager>();
            Console.WriteLine($"已注册 Hook 处理器数量: {hookManager.GetHandlerCount(HookPoints.ToolBeforeExecute)}");
            
            var hookResult = await hookManager.TriggerAsync(HookPoints.ToolBeforeExecute, 
                new Dictionary<string, object> { ["tool"] = "calculator" });
            Console.WriteLine($"Hook 触发结果: Continue={hookResult.Continue}");
            Console.WriteLine();

            // 7. ACP Agent 示例
            Console.WriteLine("=== ACP 协议示例 ===");
            var acpAgent = serviceProvider.GetRequiredService<IAcpAgent>();
            
            // 初始化
            var initResponse = await acpAgent.InitializeAsync(20250326);
            Console.WriteLine($"ACP 初始化: {initResponse.AgentInfo.Name} v{initResponse.AgentInfo.Version}");
            
            // 认证
            var authResponse = await acpAgent.AuthenticateAsync("default");
            Console.WriteLine($"ACP 认证: Authenticated={authResponse?.Authenticated ?? false}");
            
            // 创建 Session
            var sessionResponse = await acpAgent.NewSessionAsync("./");
            Console.WriteLine($"ACP 新 Session: {sessionResponse.SessionId}");
            
            // 发送 Prompt
            var promptResponse = await acpAgent.PromptAsync(
                new List<ContentBlock> { new TextContentBlock("Hello, ACP Agent!") },
                sessionResponse.SessionId);
            Console.WriteLine($"ACP Prompt 响应: {(promptResponse.Content.FirstOrDefault() as TextContentBlock)?.Text}");
            
            // 列出 Sessions
            var sessionsList = await acpAgent.ListSessionsAsync();
            Console.WriteLine($"ACP Sessions 数量: {sessionsList.Sessions?.Count ?? 0}");
            
            // Fork Session
            var forkResponse = await acpAgent.ForkSessionAsync("./", sessionResponse.SessionId);
            Console.WriteLine($"ACP Fork Session: {forkResponse.SessionId}");
            
            // Resume Session
            var resumeResponse = await acpAgent.ResumeSessionAsync("./", sessionResponse.SessionId);
            Console.WriteLine($"ACP Resume Session: {resumeResponse.SessionId}");
            
            Console.WriteLine("\n=== 示例完成 ===");
        }
    }

    /// <summary>
    /// 日志 Hook 处理器示例
    /// </summary>
    public class LogHookHandler : IHookHandler
    {
        public string HookPoint => HookPoints.ToolBeforeExecute;
        public int Priority => 10;

        public async Task<HookResult> ExecuteAsync(HookContext context)
        {
            var toolName = context.Data.TryGetValue("tool", out var tool) ? tool : "unknown";
            Console.WriteLine($"[Hook] 工具即将执行: {toolName}");
            return new HookResult { Continue = true };
        }
    }

    /// <summary>
    /// 计算器 Tool 示例
    /// </summary>
    public class CalculatorTool : ITool
    {
        public string Id => "calculator";
        public string Description => "简单计算器 - 执行基本数学运算";

        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                operation = new { type = "string", description = "运算类型: add, subtract, multiply, divide" },
                a = new { type = "number", description = "第一个数字" },
                b = new { type = "number", description = "第二个数字" }
            },
            required = new[] { "operation", "a", "b" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var operation = arguments.GetProperty("operation").GetString();
            var a = arguments.GetProperty("a").GetDouble();
            var b = arguments.GetProperty("b").GetDouble();

            double result = 0;
            switch (operation)
            {
                case "add": result = a + b; break;
                case "subtract": result = a - b; break;
                case "multiply": result = a * b; break;
                case "divide": result = b != 0 ? a / b : double.NaN; break;
                default: return new ToolResult { Success = false, Title = "错误", Output = $"未知运算: {operation}" };
            }

            return new ToolResult
            {
                Success = true,
                Title = "计算结果",
                Output = $"{a} {operation} {b} = {result}"
            };
        }
    }

    /// <summary>
    /// ACP Agent 示例实现 - 实现 IAcpAgent 完整接口
    /// </summary>
    public class SampleAcpAgent : IAcpAgent
    {
        private readonly ILogger<SampleAcpAgent> _logger;
        private readonly HookManager _hookManager;
        private readonly SkillManager _skillManager;
        private readonly RuleEngine _ruleEngine;
        private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
        private IClient? _client;

        public SampleAcpAgent(
            ILogger<SampleAcpAgent> logger,
            HookManager hookManager,
            SkillManager skillManager,
            RuleEngine ruleEngine)
        {
            _logger = logger;
            _hookManager = hookManager;
            _skillManager = skillManager;
            _ruleEngine = ruleEngine;
        }

        // IAgentLifecycle 实现
        public async Task<InitializeResponse> InitializeAsync(
            int protocolVersion,
            ClientCapabilities? clientCapabilities = null,
            Implementation? clientInfo = null,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ACP Initialize: v{Version}", protocolVersion);
            
            await _hookManager.TriggerAsync(HookPoints.AgentBeforeInvoke, 
                new Dictionary<string, object> { ["action"] = "initialize" });
            
            return new InitializeResponse
            {
                ProtocolVersion = protocolVersion,
                AgentCapabilities = new AgentCapabilities
                {
                    LoadSession = true,
                    PromptCapabilities = new PromptCapabilities { Audio = true, Image = true }
                },
                AgentInfo = Implementation.Create("Seeing.Agent", "1.0.0")
            };
        }

        public async Task<AuthenticateResponse?> AuthenticateAsync(
            string methodId,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ACP Authenticate: {Method}", methodId);
            return new AuthenticateResponse { Authenticated = true };
        }

        public void OnConnect(IClient client)
        {
            _client = client;
            _logger.LogInformation("ACP OnConnect");
        }

        // IPromptHandler 实现
        public async Task<PromptResponse> PromptAsync(
            IEnumerable<ContentBlock> prompt, 
            string sessionId, 
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ACP Prompt: {SessionId}", sessionId);
            
            await _hookManager.TriggerAsync(HookPoints.AgentBeforeInvoke, 
                new Dictionary<string, object> { ["sessionId"] = sessionId });
            
            // 获取输入文本
            var inputText = prompt.FirstOrDefault(p => p is TextContentBlock) as TextContentBlock;
            var text = inputText?.Text ?? "";
            
            // 获取已发现的技能信息
            var skills = _skillManager.GetAllSkillInfos();
            var skillNames = string.Join(", ", skills.Keys);
            
            await _hookManager.TriggerAsync(HookPoints.AgentAfterInvoke, 
                new Dictionary<string, object> { ["sessionId"] = sessionId });
            
            return new PromptResponse
            {
                StopReason = StopReasons.EndTurn,
                Content = new List<ContentBlock> { 
                    new TextContentBlock($"收到输入: {text}\n可用技能: {(skillNames.Length > 0 ? skillNames : "无")}") 
                }
            };
        }

        public async Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ACP Cancel: {SessionId}", sessionId);
        }

        // ISessionManagement 实现
        public async Task<NewSessionResponse> NewSessionAsync(
            string cwd, 
            List<McpServerConfig>? mcpServers = null, 
            CancellationToken cancellationToken = default)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            _logger.LogInformation("ACP NewSession: {SessionId}", sessionId);
            
            _sessions[sessionId] = new SessionData { Cwd = cwd, Id = sessionId };
            
            await _hookManager.TriggerAsync(HookPoints.SessionCreated, 
                new Dictionary<string, object> { ["sessionId"] = sessionId, ["cwd"] = cwd });
            
            return new NewSessionResponse { SessionId = sessionId };
        }

        public async Task<LoadSessionResponse?> LoadSessionAsync(
            string cwd, 
            string sessionId, 
            List<McpServerConfig>? mcpServers = null, 
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ACP LoadSession: {SessionId}", sessionId);
            
            if (!_sessions.TryGetValue(sessionId, out var session))
                return null;
            
            return new LoadSessionResponse { SessionId = sessionId };
        }

        public async Task<ListSessionsResponse> ListSessionsAsync(
            string? cursor = null, 
            string? cwd = null, 
            CancellationToken cancellationToken = default)
        {
            var sessions = _sessions.Values
                .Where(s => cwd == null || s.Cwd == cwd)
                .Select(s => new SessionInfo { Id = s.Id, Cwd = s.Cwd })
                .ToList();
            
            return new ListSessionsResponse { Sessions = sessions };
        }

        public async Task<ForkSessionResponse> ForkSessionAsync(
            string cwd, 
            string sessionId, 
            List<McpServerConfig>? mcpServers = null, 
            CancellationToken cancellationToken = default)
        {
            var newSessionId = Guid.NewGuid().ToString("N");
            _sessions[newSessionId] = new SessionData { Cwd = cwd, Id = newSessionId };
            
            _logger.LogInformation("ACP ForkSession: {OldId} -> {NewId}", sessionId, newSessionId);
            return new ForkSessionResponse { SessionId = newSessionId };
        }

        public async Task<ResumeSessionResponse> ResumeSessionAsync(
            string cwd, 
            string sessionId, 
            List<McpServerConfig>? mcpServers = null, 
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ACP ResumeSession: {SessionId}", sessionId);
            return new ResumeSessionResponse { SessionId = sessionId };
        }

        // ISessionConfig 实现
        public async Task<SetSessionModeResponse?> SetSessionModeAsync(
            string modeId, 
            string sessionId, 
            CancellationToken cancellationToken = default)
        {
            return new SetSessionModeResponse { ModeId = modeId };
        }

        public async Task<SetSessionModelResponse?> SetSessionModelAsync(
            string modelId, 
            string sessionId, 
            CancellationToken cancellationToken = default)
        {
            return new SetSessionModelResponse { ModelId = modelId };
        }

        public async Task<SetSessionConfigOptionResponse?> SetConfigOptionAsync(
            string configId, 
            string value, 
            string sessionId, 
            CancellationToken cancellationToken = default)
        {
            return new SetSessionConfigOptionResponse { ConfigId = configId };
        }

        // IAgentExtensions 实现
        public async Task<Dictionary<string, object?>> ExtMethodAsync(
            string method, 
            Dictionary<string, object?> parameters, 
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ACP ExtMethod: {Method}", method);
            return new Dictionary<string, object?> { ["result"] = "ok" };
        }

        public async Task ExtNotificationAsync(
            string method, 
            Dictionary<string, object?> parameters, 
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ACP ExtNotification: {Method}", method);
        }
    }

    /// <summary>
    /// 内部 Session 数据存储
    /// </summary>
    internal class SessionData
    {
        public string Id { get; set; } = "";
        public string Cwd { get; set; } = "";
    }
}