using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 真实浏览器用例的串行集合：同一台机器上同时只跑一个宿主 / Chromium。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须串行</b>：这些用例各自拉起一个真实宿主（含 Chromium 与渲染进程）。
/// 默认并行时会有 4–6 个浏览器同时启动，把 CPU / 内存吃满 ——
/// 结果不只是慢：依赖「预算 / 帧率」的用例会因为调度延迟而假失败
/// （例如静帧预算 800ms 的用例实测被拖到 8–12s），把一个正确的实现报成缺陷。
/// </para>
/// <para>
/// <b>为什么 <c>DisableParallelization = true</c></b>：它让本集合与<b>所有</b>其它集合同样不并行，
/// 从而把整机负载压到「一个浏览器」。代价是真实浏览器用例串行执行；
/// 不设 <c>SEEING_BROWSER_TESTS=1</c> 时它们立即返回，不会拖慢常规测试。
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrowserHostCollection
{
    /// <summary>集合名（各真实浏览器用例类都引用它）。</summary>
    public const string Name = "browser-host";
}
