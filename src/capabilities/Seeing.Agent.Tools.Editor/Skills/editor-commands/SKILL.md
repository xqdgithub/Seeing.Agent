---
name: editor-commands
description: 操作用户 WebUI 中 Monaco 编辑器（格式化/折叠/跳转/选区/多光标等）并查询其状态。需要用户在 WebUI 打开文件面板。
---

# 编辑器命令技能

## 工具
- `editor_command`：执行 Monaco 命令（透传 commandId）。参数：commandId（必需）、args（可选）、path（可选，在当前面板内切换文件）。
- `editor_query`：读取编辑器状态。kind ∈ selection | cursor | diagnostics | visibleRanges | value。
- `editor_list_commands`：列出当前编辑器支持的全部命令（id/label）。

## 常用命令（可能随 Monaco 版本漂移）
- 视图/导航：editor.action.revealDefinition、editor.action.goToLocations、editor.action.showHover、editor.action.marker.next
- 折叠：editor.foldAll、editor.unfoldAll、editor.foldLevel1
- 选区/多光标：editor.action.selectHighlights、editor.action.insertCursorAbove、editor.action.insertCursorBelow、editor.action.addSelectionToNextFindMatch
- 编辑（改内容，会标记 dirty 但不自动保存）：editor.action.formatDocument、editor.action.commentLine、editor.action.moveLinesUpAction、editor.action.moveLinesDownAction、editor.action.indentLines、editor.action.outdentLines
- 查找：actions.find、editor.action.startFindReplaceAction

## 注意
- 仅在用户当前激活的文件面板可用；无面板 → 快速失败。
- 命令改内容后需用户 Ctrl+S 保存。
- 目录查不到时用 `editor_list_commands` 动态探测。
