using Seeing.Agent.HostComposition;
using Seeing.Agent.WebUI.Hosting;

// 宿主子模式：客户端会用「当前进程的可执行文件」拉起它，
// 因此必须在构建 WebUI 之前把开关接走（否则会落到正常 UI 启动）。
if (args.Contains(BrowserHostRunner.BrowserHostSwitch, StringComparer.Ordinal)
    || args.Contains(BrowserHostRunner.WatchdogSwitch, StringComparer.Ordinal))
{
    return await BrowserHostRunner.RunAsync(args, CancellationToken.None);
}

// 浏览器宿主入口：只负责「组装 → 运行」。
// 组装逻辑在 WebUiHost（单一真相源），桌面宿主 Seeing.Agent.Desktop 复用同一个 BuildAsync。

var app = await WebUiHost.BuildAsync(args);
await app.RunAsync();

return 0;
