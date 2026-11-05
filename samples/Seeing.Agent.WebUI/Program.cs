using Seeing.Agent.WebUI.Hosting;

// 浏览器宿主入口：只负责「组装 → 运行」。
// 组装逻辑在 WebUiHost（单一真相源），桌面宿主 Seeing.Agent.Desktop 复用同一个 BuildAsync。

var app = await WebUiHost.BuildAsync(args);
await app.RunAsync();
