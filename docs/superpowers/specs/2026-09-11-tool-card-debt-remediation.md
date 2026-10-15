# 工具卡历史债处理（2026-09-11）

配套 bash 终端卡架构审查债清理。

| 债 | 处理 |
|----|------|
| D1 MetadataSink 悬空 | 文档标明未接线；删除 Bash/Task 无效 `SetMetadata`；进度一律 EventSink |
| D2 Duration 三通道 | `SessionToolCall.DurationMs` 一等字段；停止写入 `Metadata["durationMs"]`；旧会话可读袋键兼容 |
| D3 VM 膨胀 | `BashCardModel` 投影；VM 仅保留通用 `Metadata` + `IsBashTool` |
| D4 Task 启发式 | `IsTaskTool` = Name==task \|\| 显式 TaskId |
| D5 双写 | 约定 Metadata 为 UI 真源；`<bash_metadata>` 仅 LLM 附录 + strip |
| D6 静默 catch | Running emit 失败 `LogDebug` |

详见 `docs/architecture/05-extension-guide.md` §11。
