# 提示词架构重构 - 学习记录

## 已完成的变更

### 新增文件
- `src/Seeing.Agent/Core/Prompts/SystemPromptProvider.cs` - 系统提示词提供者，根据 Provider/Model 选择模板
- `src/Seeing.Agent/Core/Prompts/PromptBuilder.cs` - 统一提示词构建器，支持异步 BuildAsync + 同步 Build

### 修改文件
- `src/Seeing.Agent/Core/Prompts/PromptContext.cs` - 新增 ProviderId, ModelVariant, WorkspaceRoot, Platform, Agent, Services 属性
- `src/Seeing.Agent/Seeing.Agent.csproj` - 添加 Templates/*.txt 为 EmbeddedResource
- `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` - 添加 AddPromptBuilder() 方法，在 RegisterCoreServices 中调用
- `src/Seeing.Agent/Core/Models/AgentModels.cs` - AgentContext 新增 WorkspaceRoot 属性，CreateSubAgentContext 中复制

## 关键决策
1. IInstructionLoader 之前未在 DI 中注册，现在在 AddPromptBuilder 中注册为 Singleton
2. PromptBuilder 的 Build() 同步方法已添加环境信息替换（原 DynamicPromptBuilder 缺少此功能）
3. 模板资源名格式如 `Seeing.Agent.Core.Prompts.Templates.default.txt`，通过倒数第二段提取模板名
4. beast 模板用于 gpt-4/o1/o3 模型

## 构建验证
- `dotnet build src/Seeing.Agent/Seeing.Agent.csproj` - 0 错误，837 警告（全部为已有 XML 文档注释和 CA1416 跨平台警告）
