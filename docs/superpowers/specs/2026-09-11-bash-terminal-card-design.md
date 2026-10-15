# Bash 终端展示卡设计

**日期:** 2026-09-11  
**范围:** WebUI 只读展示；无 PTY / stdin

## 目标

`bash` 与普通工具卡分流，提供终端风只读卡片：流式输出、exit/超时/取消、耗时、workdir、复制、折叠、贴底滚动、换行切换。

## 数据流

```
BashTool (EventSink Running + ToolResult Metadata)
  → AgentExecutor (Title/Metadata on Complete)
  → ToolCallEvent
  → EventStreamHandler + ChatEventTracker → SessionToolCall
  → ToolCallViewModel.FromSessionToolCall
  → BashMessageComponent (priority 45)
```

## 非目标

- 交互式输入 / PTY
- ANSI 彩色渲染（输出侧继续剥离）
- 其它工具的专用卡

## 约定

- UI 真源：`ToolResult.Metadata` → 事件/Session Metadata（exit 等）
- Output 中 `<bash_metadata>` 仅为 LLM 可读附录；`BashToolDisplayHelper.StripBashMetadataBlock` 剥离
- 进度：`EventSink` / `ToolCallRunning`；勿依赖 `IToolMetadataSink`
