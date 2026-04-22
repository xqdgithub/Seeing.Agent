# 计划学习记录 - webui-session-refactor

- 已完成: 更新 DI 注册以暴露新的会话相关服务。
- 注册项：ISessionEventPublisher -> SessionEventPublisher（单例），ISessionLifecycle -> SessionLifecycle（单例）
- 保留现有 SessionManager 注册，不修改现有依赖关系。
- 构建检查:
  - dotnet build src/Seeing.Session → PASS
  - dotnet build src/Seeing.Agent → PASS
- 测试检查:
  - dotnet test tests/Seeing.Session.Tests → 失败，原因：SessionStatus.Running 不存在于当前 SessionStatus 枚举。需要将测试用到的状态名称与实现对齐，或在实现中增加 Running 状态（若设计允许）。

- 下步计划:
  1) 与测试团队确认 SessionStatus 的枚举设计是否应包含 Running 状态，若应包含则在 SessionStatus 枚举中添加 Running；否则修改测试用例以使用现有状态（如 Created/Active/Idle/Completed/Archived/Error）。
  2) 如需更改实现以兼容测试，需评估对现有状态机的影响，确保生命周期相关逻辑仍然正确。
  3) 将此变更在本地通过后，执行完整的回归测试，确保 Session 子系统的行为符合预期。

记录日期: 2026-04-22
