# Tools System - Annotation-based Tool Discovery

Annotation-driven tool registration and invocation for LLM function calling.

## STRUCTURE

```
Tools/
├── Attributes/
│   └── ToolAttributes.cs    # [Tool], [ToolParam], [Required], [ToolParamType]
│
├── Discovery/
│   ├── ToolDiscovery.cs     # Reflection scanner + JSON Schema builder
│   └── ReflectedTool.cs     # Method-to-ITool wrapper + parameter conversion
│
├── BuiltIn/
│   ├── FileSystem/          # Read/Write/Edit/Glob/Grep 工具
│   │   ├── FileSystemHelper.cs # 路径安全、MIME 类型、截断（679 行）
│   │   ├── OutputTruncator.cs  # 输出限制
│   │   └── BinaryFileDetector.cs # 二进制检测
│   ├── Shell/               # Bash 工具
│   ├── Web/                 # WebFetch/WebSearch 工具
│   ├── SubTask/             # Task 工具（委托子 Agent）
│   ├── Todo/                # TodoWrite 工具
│   └ BuiltInToolBase.cs     # 内置工具基类（权限请求模板）
│
├── ToolInvoker.cs           # 统一调用器（权限检查、重试、Hook）
└── SkillTool.cs             # 技能加载工具
```

## WHERE TO LOOK

| Task | File | Notes |
|------|------|-------|
| Add new attribute | `ToolAttributes.cs` | Extend `AttributeTargets` as needed |
| Change discovery logic | `ToolDiscovery.cs` | `DiscoverTools()` entry point |
| Fix parameter conversion | `ReflectedTool.cs` | `ConvertParameter()` handles type coercion |
| Hook integration | `ToolInvoker.cs` | Triggers `tool.before_execute`, etc. |
| Path safety | `BuiltIn/FileSystemHelper.cs` | `IsWithinWorkingDirectory()` |
| Output limits | `BuiltIn/OutputTruncator.cs` | 2000 行 / 50KB |
| Binary detection | `BuiltIn/BinaryFileDetector.cs` | 30% 非打印字符阈值 |
| Permission request | `BuiltIn/BuiltInToolBase.cs` | `AskPermissionAsync()` |

## CONVENTIONS (Annotation Patterns)

**Method marking:**
```csharp
[Tool("描述文本", Name = "可选自定义ID")]
public static async Task<string> MethodName(...) { }
```

**Parameter marking:**
```csharp
[ToolParam("参数描述")]
[Required]  // 无默认值或显式标记
string paramName
```

**Registration patterns:**
```csharp
// From static class
toolInvoker.RegisterToolsFromType<MyStaticTools>();

// From instance class (requires DI or parameterless ctor)
toolInvoker.RegisterToolsFromType<MyInstanceTools>();

// Manual ITool registration
toolInvoker.RegisterTool(new MyTool());
```

## ANTI-PATTERNS

| Avoid | Why |
|-------|-----|
| Async void return | Must return `Task` or `Task<T>` |
| Out/ref parameters | Not supported in JSON Schema |
| Generic methods | Reflection cannot resolve type args |
| Value types without default ctor | `Activator.CreateInstance` will fail |
| Overloaded tool names | Last registered wins silently |
| 绕过 FileSystemHelper 路径检查 | 安全白名单失效 |
| 禁用 OutputTruncator | 可能内存溢出 |

## NOTES

- **内置工具**: Read/Write/Edit/Glob/Grep/Bash/WebFetch/WebSearch/Task/TodoWrite/Skill
- **限制常量**: 2000 行 / 50KB / 100 Grep 匹配 / 100 Glob 文件
- **装饰器链**: 超时（最外层）→ 重试 → 缓存（最内层）
- **权限模式**: `AgentPermissionConfig.AllowedTools/DeniedTools` 白黑名单
