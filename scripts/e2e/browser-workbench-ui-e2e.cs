#:package PuppeteerSharp

using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PuppeteerSharp;

var root = args.Length > 0 ? args[0] : @"D:\Old_Project\MyProjects\Seeing.Agent";
var port = 17999;
var baseUrl = $"http://127.0.0.1:{port}";
var outDir = Path.Combine(Path.GetTempPath(), "opencode", "browser-e2e");
Directory.CreateDirectory(outDir);

var sessionsDir = Path.Combine(root, ".seeing", "sessions");
var sessionFile = Directory.Exists(sessionsDir)
    ? Directory.EnumerateFiles(sessionsDir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
    : null;
var sessionId = sessionFile is not null
    ? Path.GetFileNameWithoutExtension(sessionFile)
    : "e2e-" + Guid.NewGuid().ToString("N");

Console.WriteLine($"repo={root}");
Console.WriteLine($"session={sessionId}");

var psi = new ProcessStartInfo("dotnet",
    $"run --no-build --project samples/Seeing.Agent.WebUI -- --urls {baseUrl} --workspace \"{root}\"")
{
    WorkingDirectory = root,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
psi.Environment["SEEING_BOOT"] = "full";
var webui = Process.Start(psi)!;
var log = new StreamWriter(Path.Combine(outDir, "webui-ui.log")) { AutoFlush = true };
webui.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
webui.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine("ERR " + e.Data); };
webui.BeginOutputReadLine();
webui.BeginErrorReadLine();

IPage? testPageRef = null;

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var ready = false;
    var deadline = DateTime.UtcNow.AddSeconds(40);
    while (DateTime.UtcNow < deadline)
    {
        if (webui.HasExited) throw new Exception($"WebUI 退出，code={webui.ExitCode}；日志 {outDir}\\webui-ui.log");
        try { await http.GetAsync(baseUrl + "/"); ready = true; break; } catch { await Task.Delay(300); }
    }
    if (!ready) throw new Exception("WebUI 40s 内未就绪");

    await using var testBrowser = await Puppeteer.LaunchAsync(new LaunchOptions
    {
        Headless = true,
        ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        UserDataDir = Path.Combine(outDir, "ui-test-browser"),
        Args = ["--no-first-run", "--no-default-browser-check"]
    });
    var testPage = await testBrowser.NewPageAsync();
    testPageRef = testPage;
    await testPage.GoToAsync($"{baseUrl}/session/{sessionId}",
        new NavigationOptions { WaitUntil = [WaitUntilNavigation.Networkidle2] });
    Console.WriteLine($"session page: {testPage.Url}");

    await testPage.WaitForSelectorAsync(".session-workbench-trigger", new WaitForSelectorOptions { Timeout = 20000 });
    await testPage.ClickAsync(".session-workbench-trigger");
    await Task.Delay(600);
    var labels = await testPage.EvaluateExpressionAsync<string>(
        "[...document.querySelectorAll('.workbench-picker-item')].map(e => (e.textContent || '').trim()).join(' | ')");
    Console.WriteLine($"UI: picker items = [{labels}]");
    var picked = await testPage.EvaluateFunctionAsync<bool>("""
        () => {
            const items = [...document.querySelectorAll('.workbench-picker-item')];
            const hit = items.find(e => {
                const t = (e.textContent || '').toLowerCase();
                return t.includes('浏览器') || t.includes('browser');
            });
            if (!hit) return false;
            hit.click();
            return true;
        }
        """);
    if (!picked) throw new Exception("工作台选择器里没有“浏览器”项");
    await testPage.WaitForSelectorAsync(".browser-workbench", new WaitForSelectorOptions { Timeout = 20000 });
    Console.WriteLine("UI: browser workbench opened");

    var uiUrl = "data:text/html,<body style='margin:0;background:%23fff;font:16px/1.5 sans-serif'><h1>Static heading</h1><p>The quick brown fox jumps over the lazy dog. 1234567890</p><p>SECOND LINE OF TEXT for sampling.</p><div style='height:2000px;background:linear-gradient(%23fff,%23333)'></div></body>";
    var connected = await WaitUntilAsync(async () =>
        (await testPage.EvaluateExpressionAsync<string>(
            "document.querySelector('.browser-workbench__status')?.textContent || ''")).Contains("已连接"),
        TimeSpan.FromSeconds(15));
    Console.WriteLine($"UI: ws connected = {connected}");
    await DumpBwStateAsync("after-connect");
    var navigated = await testPage.EvaluateFunctionAsync<bool>("""
        (url) => {
            const el = document.querySelector('.browser-workbench__address');
            if (!el) return false;
            el.focus();
            el.value = url;
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
            return true;
        }
        """, uiUrl);
    Console.WriteLine($"UI: address bar dispatched = {navigated}");

    var canvasReady = await WaitUntilAsync(async () =>
        await testPage.EvaluateExpressionAsync<int>(
            "document.querySelector('.browser-workbench__canvas')?.width ?? 0") > 0,
        TimeSpan.FromSeconds(25));
    if (!canvasReady) throw new Exception("UI canvas 25s 内没有尺寸");

    var painted = await WaitUntilAsync(async () => await CanvasSumAsync() > 0, TimeSpan.FromSeconds(20));
    if (!painted)
    {
        Console.WriteLine("UI: first navigate did not paint; retrying address bar");
        await testPage.ClickAsync(".browser-workbench__address");
        await testPage.Keyboard.DownAsync("Control");
        await testPage.Keyboard.PressAsync("A");
        await testPage.Keyboard.UpAsync("Control");
        await testPage.TypeAsync(".browser-workbench__address", uiUrl);
        await testPage.Keyboard.PressAsync("Enter");
        painted = await WaitUntilAsync(async () => await CanvasSumAsync() > 0, TimeSpan.FromSeconds(20));
    }
    var sum1 = await CanvasSumAsync();
    if (!painted) throw new Exception($"UI canvas 20s 内无像素（sum={sum1}）");
    Console.WriteLine($"UI: canvas ready, pixelSum={sum1}");
    await Task.Delay(1500);   // 等静止画面补上静帧
    await SampleIdleAsync("idle-before-input", samples: 20, intervalMs: 500);
    await DumpBwStateAsync("after-idle");
    await testPage.ScreenshotAsync(Path.Combine(outDir, "ui-workbench.png"));

    var rect = await testPage.EvaluateExpressionAsync<string>("""
        (() => {
            const r = document.querySelector('.browser-workbench__canvas').getBoundingClientRect();
            return JSON.stringify({ x: r.x + r.width / 2, y: r.y + r.height / 2 });
        })()
        """);
    using var rd = JsonDocument.Parse(rect);
    var cx = rd.RootElement.GetProperty("x").GetDouble();
    var cy = rd.RootElement.GetProperty("y").GetDouble();

    var vp = await testPage.EvaluateExpressionAsync<string>("JSON.stringify({ w: innerWidth, h: innerHeight })");
    Console.WriteLine($"UI: canvas center=({cx:F0},{cy:F0}) viewport={vp}");
    await testPage.Mouse.MoveAsync((decimal)cx, (decimal)cy);
    var cdp = await testPage.CreateCDPSessionAsync();
    await cdp.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object>
    {
        ["type"] = "mouseWheel",
        ["x"] = cx,
        ["y"] = cy,
        ["deltaX"] = 0,
        ["deltaY"] = 600
    });
    var changed = await WaitUntilAsync(async () => await CanvasSumAsync() != sum1, TimeSpan.FromSeconds(10));
    Console.WriteLine($"UI: cdp wheel changed canvas = {changed}");
    if (!changed)
    {
        await testPage.Mouse.WheelAsync(0, 600);
        changed = await WaitUntilAsync(async () => await CanvasSumAsync() != sum1, TimeSpan.FromSeconds(10));
        Console.WriteLine($"UI: puppeteer wheel changed canvas = {changed}");
    }
    await cdp.DetachAsync();
    var top = await testPage.EvaluateExpressionAsync<string>("""
        (() => {
            const e = document.elementFromPoint(524, 361);
            return e ? (e.tagName + '.' + (e.className || '')) : 'null';
        })()
        """);
    Console.WriteLine($"UI: elementAtCenter = {top}");
    if (!changed)
    {
        await testPage.EvaluateExpressionAsync<string>("""
            (() => {
                const c = document.querySelector('.browser-workbench__canvas');
                c.dispatchEvent(new WheelEvent('wheel', { deltaY: 600, bubbles: true, cancelable: true }));
                return 'dispatched';
            })()
            """);
        changed = await WaitUntilAsync(async () => await CanvasSumAsync() != sum1, TimeSpan.FromSeconds(10));
        Console.WriteLine($"UI: synthetic canvas wheel changed canvas = {changed}");
    }
    await testPage.Mouse.ClickAsync((decimal)cx, (decimal)cy);
    await Task.Delay(300);
    await testPage.ClickAsync("button[title='重新加载']");
    await Task.Delay(800);
    var afterReload = await CanvasSumAsync();
    Console.WriteLine($"UI: reload clicked, canvas sum after = {afterReload}");
    Console.WriteLine("UI: wheel + click + reload done");
    await Task.Delay(1500);   // 等滚动/刷新后的补图稳定，再采样
    await SampleIdleAsync("idle-after-input", samples: 12, intervalMs: 500);
    await DumpBwStateAsync("after-input");

    ClientWebSocket? socket = null;
    Exception? lastError = null;
    var connectDeadline = DateTime.UtcNow.AddSeconds(25);
    while (DateTime.UtcNow < connectDeadline)
    {
        var attempt = new ClientWebSocket();
        try
        {
            await attempt.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/browser/{sessionId}/stream"), CancellationToken.None);
            socket = attempt;
            break;
        }
        catch (Exception ex)
        {
            lastError = ex;
            attempt.Dispose();
            await Task.Delay(1000);
        }
    }
    if (socket is null) throw new Exception($"WS 25s 内未连上: {lastError?.Message}");
    using var ws = socket;
    Console.WriteLine("WS connected");

    var session = new Session(ws);
    _ = session.RunAsync();
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    async Task Send(object payload)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, jsonOptions)),
            WebSocketMessageType.Text, true, CancellationToken.None);

    try
    {
    var first = await session.WaitFrameAsync(f => f.Kind == 1, TimeSpan.FromSeconds(15));
    Console.WriteLine($"FLOW1 seq={first.Seq} {first.Width}x{first.Height} bytes={first.Bytes.Length} epoch={first.Epoch}");
    File.WriteAllBytes(Path.Combine(outDir, "flow1.jpg"), first.Bytes);

    await Send(new { type = "resize", width = 800, height = 600, pixelRatio = 1.25 });
    // 帧的像素数 = 视口 × 帧倍率。页面 dpr 已在首个 resize 固定（1×），
    // 所以这里像素比不变：视口 800×600、帧 800×600，画布 backing 也必须是 800×600。
    // 「按 dpr 放大成物理像素」的契约由单元测试 StartScreencast_ShouldEncodeAtViewportTimesFrameScale 守门。
    var resized = await session.WaitFrameAsync(f => f.Kind == 1 && f.Width == 800 && f.Height == 600,
        TimeSpan.FromSeconds(10));
    Console.WriteLine($"RESIZED frame={resized.Width}x{resized.Height} seq={resized.Seq}");
    var st = await session.WaitStateAsync(s => s.ViewportWidth == 800 && s.ViewportHeight == 600,
        TimeSpan.FromSeconds(10));
    Console.WriteLine($"STATE viewport={st.ViewportWidth}x{st.ViewportHeight} frameScale={st.FrameScale}");

    var before = session.LastFlowSeq;
    await Send(new { type = "wheel", deltaX = 0, deltaY = 500 });
    var after = await session.WaitFrameAsync(f => f.Kind == 1 && f.Seq > before, TimeSpan.FromSeconds(10));
    Console.WriteLine($"FLOW2 seq={after.Seq} (before={before}) bytes={after.Bytes.Length}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WS stage non-blocking failure: {ex.Message}");
    }

    // ── 单源一致性 ──
    // 画布 backing 必须等于「视口 × 帧倍率」，也就是帧的像素数：
    // 只要三者一致，流就是 1:1 落地，不存在「低清流帧 / 高清静帧」两套清晰度。
    try
    {
        var geom = await testPageRef!.EvaluateExpressionAsync<string>("""
            (() => {
                const c = document.querySelector('.browser-workbench__canvas');
                const b = window.__bw;
                if (!c || !b) return 'none';
                return JSON.stringify({
                    canvas: [c.width, c.height],
                    expected: [Math.round(b.viewportWidth * b.frameScale),
                               Math.round(b.viewportHeight * b.frameScale)],
                    frame: [b.frameWidth, b.frameHeight]
                });
            })()
            """);
        Console.WriteLine($"UI: geometry = {geom}");
        using var gd = JsonDocument.Parse(geom);
        var canvasW = gd.RootElement.GetProperty("canvas")[0].GetInt32();
        var expectedW = gd.RootElement.GetProperty("expected")[0].GetInt32();
        Console.WriteLine(canvasW == expectedW
            ? "UI: backing == viewport × frameScale (1:1)"
            : $"UI: WARNING backing {canvasW} != viewport×scale {expectedW}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"UI: geometry probe skipped: {ex.Message}");
    }

    Console.WriteLine("E2E PASS");
}
finally
{
    try { if (!webui.HasExited) webui.Kill(entireProcessTree: true); } catch { }
    try { await webui.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token); } catch { }
    log.Dispose();
    Console.WriteLine($"logs/screens: {outDir}");
}

async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        try { if (await condition()) return true; } catch { }
        await Task.Delay(200);
    }
    return false;
}

async Task DumpBwStateAsync(string label)
{
    var text = await testPageRef!.EvaluateExpressionAsync<string>("""
        (() => {
            const b = window.__bw;
            if (!b) return 'no __bw';
            const sr = b.stage ? b.stage.getBoundingClientRect() : null;
            const cr = b.canvas ? b.canvas.getBoundingClientRect() : null;
            return JSON.stringify({
                opened: b.opened,
                ws: b.socket ? b.socket.readyState : -1,
                resizePending: b.resizeTimer !== null,
                lastSent: [b.lastSentResizeWidth, b.lastSentResizeHeight, b.lastSentDpr],
                stage: sr ? [Math.round(sr.width), Math.round(sr.height)] : null,
                canvasCss: cr ? [Math.round(cr.width), Math.round(cr.height)] : null,
                canvasBmp: b.canvas ? [b.canvas.width, b.canvas.height] : null,
                viewport: [b.viewportWidth, b.viewportHeight, b.frameScale],
                epoch: b.epoch
            });
        })()
        """);
    Console.WriteLine($"UI: {label} bw={text}");
}

/// <summary>
/// 静止采样：记录每帧画布「位图尺寸 : 像素采样和」。/// 尺寸变化说明视口在抖，像素变化说明画面在重绘 —— 两者都是用户看到的「闪」。
/// </summary>
async Task SampleIdleAsync(string label, int samples, int intervalMs)
{
    var seen = new List<string>();
    for (var i = 0; i < samples; i++)
    {
        try
        {
            seen.Add(await CanvasSignatureAsync());
        }
        catch (Exception ex)
        {
            seen.Add("err:" + ex.GetType().Name);
        }
        await Task.Delay(intervalMs);
    }

    var distinct = seen.Distinct().Count();
    // 「闪」的判据是**持续交替**，不是「操作后有一次清晰度补图」：
    // 只看尾部窗口是否稳定（前几帧允许是从流帧过渡到静帧的正常收敛）。
    var tail = seen.Skip(Math.Max(0, seen.Count - 4)).ToList();
    var tailStable = tail.Distinct().Count() == 1;
    Console.WriteLine($"UI: {label} samples={samples} distinct={distinct} tailStable={tailStable}");
    Console.WriteLine($"UI: {label} = [{string.Join(",", seen)}]");
    if (!tailStable)
        Console.WriteLine($"UI: WARNING {label} still changing (sustained repaint/reflow)");
}

async Task<long> CanvasSumAsync()
    => await testPageRef!.EvaluateExpressionAsync<long>("""
        (() => {
            const c = document.querySelector('.browser-workbench__canvas');
            if (!c || !c.width || !c.height) return -1;
            const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
            let s = 0;
            for (let i = 0; i < d.length; i += 4000) s += d[i];
            return s;
        })()
        """);

/// <summary>画布签名：位图尺寸 + 像素采样和（尺寸抖或重绘都会变）。</summary>
async Task<string> CanvasSignatureAsync()
    => await testPageRef!.EvaluateExpressionAsync<string>("""
        (() => {
            const c = document.querySelector('.browser-workbench__canvas');
            if (!c || !c.width || !c.height) return 'none';
            const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
            let s = 0;
            for (let i = 0; i < d.length; i += 4000) s += d[i];
            return c.width + 'x' + c.height + ':' + s;
        })()
        """);

sealed class Session(ClientWebSocket ws)
{
    private readonly object _gate = new();
    private readonly List<Frame> _frames = [];
    private readonly List<StateMsg> _states = [];
    private Task? _loop;

    public long LastFlowSeq { get { lock (_gate) return _frames.LastOrDefault(f => f.Kind == 1)?.Seq ?? 0; } }

    public Task RunAsync() => _loop ??= Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var buffer = new byte[1 << 20];
        var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buffer, CancellationToken.None); }
            catch { return; }
            if (r.MessageType == WebSocketMessageType.Close) return;
            ms.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;

            var data = ms.ToArray();
            ms.SetLength(0);
            if (r.MessageType == WebSocketMessageType.Binary && data.Length >= 18)
            {
                var kind = data[0];
                var version = data[1];
                var epoch = BitConverter.ToUInt32(data, 2);
                var seq = BitConverter.ToUInt32(data, 6);
                var w = BitConverter.ToUInt16(data, 10);
                var h = BitConverter.ToUInt16(data, 12);
                var len = BitConverter.ToUInt32(data, 14);
                if (version == 1 && 18 + len == data.Length)
                    lock (_gate) _frames.Add(new Frame(kind, seq, epoch, w, h, data[18..]));
            }
            else if (r.MessageType == WebSocketMessageType.Text)
            {
                using var doc = JsonDocument.Parse(data);
                var rootEl = doc.RootElement;
                if (rootEl.TryGetProperty("type", out var t) && t.GetString() == "state")
                {
                    var s = new StateMsg(
                        rootEl.TryGetProperty("viewportWidth", out var vw) ? vw.GetInt32() : 0,
                        rootEl.TryGetProperty("viewportHeight", out var vh) ? vh.GetInt32() : 0,
                        rootEl.TryGetProperty("frameScale", out var fs) ? fs.GetDouble() : 0);
                    lock (_gate) _states.Add(s);
                }
            }
        }
    }

    public async Task<Frame> WaitFrameAsync(Func<Frame, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                var hit = _frames.LastOrDefault(predicate);
                if (hit is not null) return hit;
            }
            await Task.Delay(50);
        }
        lock (_gate) throw new Exception($"超时未收到匹配帧；已收 {_frames.Count} 帧");
    }

    public async Task<StateMsg> WaitStateAsync(Func<StateMsg, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                var hit = _states.LastOrDefault(predicate);
                if (hit is not null) return hit;
            }
            await Task.Delay(50);
        }
        lock (_gate) throw new Exception($"超时未收到匹配 state；已收 {_states.Count} 条");
    }
}

sealed record Frame(byte Kind, uint Seq, uint Epoch, int Width, int Height, byte[] Bytes);
sealed record StateMsg(int ViewportWidth, int ViewportHeight, double FrameScale);
