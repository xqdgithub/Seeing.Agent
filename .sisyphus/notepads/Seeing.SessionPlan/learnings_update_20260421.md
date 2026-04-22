2026-04-21 更新摘要
- 将 SessionStatus 枚举更新为六态：Created/Active/Idle/Completed/Archived/Error。
- 将 Unknown/Running/Paused 替换为 Created/Active/Idle，保持向前兼容性。
- 为每个状态添加了 XML 注释，提供中英文描述。
- 将 SessionData.Status 的默认值由 Unknown 改为 Created。
- 进行了构建验证：dotnet build src/Seeing.Session 成功通过，存在一些警告但不影响编译。 
- 同步更新引用位置，确保编译链路不破坏其他逻辑。
