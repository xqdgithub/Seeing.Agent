记录: 新增 Seeing.Agent.NewTui 项目结构与 csproj。遇到的构建困难：中央包管理（Directory.Packages.props）需要显式版本号，添加了 Directory.Packages.props，但仍然出现版本冲突 NU1107，Terminal.Gui 2.0.0 依赖 Microsoft.Extensions.Logging 8.x，而 Seeing.Agent 依赖 1.x，无法在当前依赖树下完成构建。
行为要点:
- 指定了 Terminal.Gui 2.0.0 版本并添加了对 Seeing.Agent 的 ProjectReference。
- 创建了 Directory.Packages.props 以满足 CPM 要求，版本为 Terminal.Gui 2.0.0。
- 尝试执行 restore 后，看到新增依赖冲突，需要计划性的依赖升级或降级。
下步计划:
- 评估 Seeing.Agent 的依赖树，考虑将 net10.0 与现有依赖兼容性进行对齐；
- 或将 Terminal.Gui 降级到与 Seeing.Agent 兼容的版本，或升级 Root 项目的 Microsoft.Extensions.* 族以匹配 Terminal.Gui 的依赖。
成功标准:
- 能在本地执行 dotnet restore 和 dotnet build，并得到无错误的输出。
