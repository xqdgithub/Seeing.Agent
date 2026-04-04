# Tools System - Annotation-based Tool Discovery

Annotation-driven tool registration and invocation for LLM function calling.

## STRUCTURE

```
Tools/
├── Attributes/
│   └── ToolAttributes.cs    # [Tool], [ToolParam], [Required]
├── Discovery/
│   ├── ToolDiscovery.cs     # Reflection scanner + JSON Schema builder
│   └── ReflectedTool.cs     # Method-to-ITool wrapper + parameter conversion
└── ToolInvoker.cs           # Unified caller (local + MCP tools)
```

## WHERE TO LOOK

| Task | File | Notes |
|------|------|-------|
| Add new attribute | `ToolAttributes.cs` | Extend `AttributeTargets` as needed |
| Change discovery logic | `ToolDiscovery.cs` | `DiscoverTools()` entry point |
| Fix parameter conversion | `ReflectedTool.cs` | `ConvertParameter()` handles type coercion |
| Hook integration | `ToolInvoker.cs` | Triggers `tool.before_execute`, etc. |

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
