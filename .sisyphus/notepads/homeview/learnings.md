Task: 实现 HomeView 首页视图，包含 Logo、输入框和 Enter 开始会话的逻辑。

- 实现要点:
  - 继承 Terminal.Gui 的 Window，放置 ASCII Logo、文本输入框(TextView)和提示文本
  - 监听 Application.Top.KeyPress，按下 Enter 时读取输入框文本，非空时启动 SessionView 并清空输入框
  - 通过 AgentRunner 发送用户输入文本
  - 启动 SessionView 时保持状态对象 AppState 和 Runner 的生命周期一致

- 设计注意:
  - 不添加动画效果
  - 不实现 Agent 选择逻辑
  - 代码风格与项目现有结构保持一致

- 验证要点:
  - 运行 dotnet build src/Seeing.Agent.NewTui，确保 HomeView.cs 编译通过
  - 运行应用时，Logo 显示在居中上方，输入框居中，按 Enter 能启动会话并清空输入框

- 后续事项:
  - 如遇依赖缺失，请在本地环境确保 Terminal.Gui 引用及相关依赖正常解析
