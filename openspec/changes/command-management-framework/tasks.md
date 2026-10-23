## 1. CommandMetadata 扩展

- [x] 1.1 在 `CommandMetadata` 类中添加 `Template` 属性（`string?`）
- [x] 1.2 在 `CommandMetadata` 类中添加 `Agent` 属性（`string?`）

## 2. CommandContext 扩展

- [x] 2.1 在 `CommandContext` 类中添加 `TargetAgent` 字段（`string?`）

## 3. CommandDiscovery Markdown 发现

- [x] 3.1 添加 `DiscoverFromMarkdownAsync(string directory, CancellationToken ct)` 方法
- [x] 3.2 添加 `ParseMarkdownCommandAsync(string filePath, CancellationToken ct)` 私有方法
- [x] 3.3 添加 `ParseCategory(string? category)` 私有方法
- [x] 3.4 添加 `FrontmatterRegex` 静态正则（复用现有模式）
- [x] 3.5 添加 `IDeserializer` 字段用于 YAML 反序列化

## 4. MarkdownCommand 内部类

- [x] 4.1 创建 `MarkdownCommand` 内部类实现 `ICommand` 接口
- [x] 4.2 实现 `ExecuteAsync` 方法，包含模板渲染逻辑
- [x] 4.3 添加 `RenderTemplate` 私有方法处理 `{{1}}`、`{{arguments}}` 占位符
- [x] 4.4 添加错误处理（模板为空、异常捕获）

## 5. CommandFrontmatter 内部类

- [x] 5.1 创建 `CommandFrontmatter` 内部类用于 YAML 反序列化
- [x] 5.2 定义 `Name`、`Description`、`Aliases`、`Category`、`Agent` 属性

## 6. CommandDispatcher 修改

- [x] 6.1 在 `HandleAsync` 方法中创建 `execContext` 时传递 `TargetAgent = command.Metadata.Agent`

## 7. 单元测试

- [x] 7.1 测试 `CommandMetadata.Template` 属性初始化和默认值
- [x] 7.2 测试 `CommandMetadata.Agent` 属性初始化和默认值
- [x] 7.3 测试 `CommandContext.TargetAgent` 传递
- [x] 7.4 测试 `DiscoverFromMarkdownAsync` 发现有效命令
- [x] 7.5 测试 `DiscoverFromMarkdownAsync` 跳过无效文件
- [x] 7.6 测试 `RenderTemplate` 位置参数替换
- [x] 7.7 测试 `RenderTemplate` `{{arguments}}` 替换
- [x] 7.8 测试 `RenderTemplate` 参数不足时使用空字符串
- [x] 7.9 测试 `MarkdownCommand.ExecuteAsync` 正常执行
- [x] 7.10 测试 `MarkdownCommand.ExecuteAsync` 模板为空时返回错误

## 8. 集成验证

- [x] 8.1 创建示例 Markdown 命令文件进行手动验证
- [x] 8.2 验证斜杠命令能正确调用 Markdown 命令
- [x] 8.3 验证 `Agent` 属性正确传递到上下文