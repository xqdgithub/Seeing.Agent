#:package PuppeteerSharp@25.15.0

using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PuppeteerSharp;

var root = args.Length > 0 ? args[0] : @"D:\Users\Administrator\.local\share\opencode\worktree\0eadad\browser-host-rework";
var port = 17999;
var baseUrl = $"http://127.0.0.1:{port}";
var outDir = Path.Combine(Path.GetTempPath(), "opencode", "browser-e2e");
Directory.CreateDirectory(outDir);

// 选一个“会话文件就在该工作区存储里”的会话。
var candidates = new (string SessionsDir, string Workspace)[]
{
    (Path.Combine(root, ".seeing", "sessions"), root),
    (Path.Combine(@"D:\Old_Project\MyProjects\Seeing.Agent", ".seeing", "sessions"),
     @"D:\Old_Project\MyProjects\Seeing.Agent"),
};
string? sessionFile = null;
var workingDirectory = root;
foreach (var (dir, ws) in candidates)
{
    var f = Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;
    if (f is not null) { sessionFile = f; workingDirectory = ws; break; }
}
var sessionId = sessionFile is not null
    ? Path.GetFileNameWithoutExtension(sessionFile)
    : "e2e-" + Guid.NewGuid().ToString("N");

Console.WriteLine($"repo={root}");
Console.WriteLine($"session={sessionId} workspace={workingDirectory}");

var psi = new ProcessStartInfo("dotnet",
    $"run --no-build --project samples/Seeing.Agent.WebUI -- --urls {baseUrl} --workspace \"{workingDirectory}\"")
{
    WorkingDirectory = root,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
psi.Environment["SEEING_BOOT"] = "full";
var webui = Process.Start(psi)!;
var log = new StreamWriter(Path.Combine(outDir, "webui.log")) { AutoFlush = true };
webui.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
webui.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine("ERR " + e.Data); };
webui.BeginOutputReadLine();
webui.BeginErrorReadLine();

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var ready = false;
    var deadline = DateTime.UtcNow.AddSeconds(40);
    while (DateTime.UtcNow < deadline)
    {
        if (webui.HasExited) throw new Exception($"WebUI 退出，code={webui.ExitCode}；日志 {outDir}\\webui.log");
        try { await http.GetAsync(baseUrl + "/"); ready = true; break; } catch { await Task.Delay(300); }
    }
    if (!ready) throw new Exception("WebUI 40s 内未就绪");

    await using var testBrowser = await Puppeteer.LaunchAsync(new LaunchOptions
    {
        Headless = true,
        ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        UserDataDir = Path.Combine(outDir, "test-browser-profile"),
        Args = ["--no-first-run", "--no-default-browser-check"]
    });
    var testPage = await testBrowser.NewPageAsync();
    await testPage.GoToAsync($"{baseUrl}/session/{sessionId}",
        new NavigationOptions { WaitUntil = [WaitUntilNavigation.Networkidle2] });
    Console.WriteLine($"session page: {testPage.Url}");

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
    if (socket is null)
    {
        Console.WriteLine("PAGE BODY >>>");
        Console.WriteLine(await testPage.EvaluateExpressionAsync<string>("document.body.innerText.slice(0,800)"));
        throw new Exception($"WS 25s 内未连上: {lastError?.Message}");
    }
    using var ws = socket;
    Console.WriteLine("WS connected");

    var session = new Session(ws);
    _ = session.RunAsync();

    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    async Task Send(object payload)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, jsonOptions)),
            WebSocketMessageType.Text, true, CancellationToken.None);

    var html = "data:text/html;charset=utf-8," + Uri.EscapeDataString("""
    <!doctype html><html><body style="margin:0;font:16px/1.6 sans-serif">
    <div style="height:900px;background:linear-gradient(#eef,#fff)">E2E TOP</div>
    <div style="height:300px;background:#d33;color:#fff">E2E MARKER</div>
    <div style="height:1600px;background:#eee">TAIL</div>
    </body></html>
    """);

    await Send(new { type = "navigate", url = html });
    var first = await session.WaitFrameAsync(f => f.Kind == 1, TimeSpan.FromSeconds(20));
    Console.WriteLine($"FLOW1 seq={first.Seq} {first.Width}x{first.Height} bytes={first.Bytes.Length} epoch={first.Epoch}");
    File.WriteAllBytes(Path.Combine(outDir, "flow1.jpg"), first.Bytes);

    await Send(new { type = "resize", width = 800, height = 600, pixelRatio = 1.25 });
    var resized = await session.WaitFrameAsync(f => f.Kind == 1 && f.Width == 800 && f.Height == 600,
        TimeSpan.FromSeconds(10));
    Console.WriteLine($"RESIZED frame={resized.Width}x{resized.Height} seq={resized.Seq}");
    session.DumpStates();

    var before = session.LastFlowSeq;
    await Send(new { type = "wheel", deltaX = 0, deltaY = 500 });
    var after = await session.WaitFrameAsync(f => f.Kind == 1 && f.Seq > before, TimeSpan.FromSeconds(10));
    Console.WriteLine($"FLOW2 seq={after.Seq} (before={before}) bytes={after.Bytes.Length}");

    await Send(new { type = "hires", rev = session.LastFlowSeq });
    var still = await session.WaitFrameAsync(f => f.Kind == 2, TimeSpan.FromSeconds(6));
    Console.WriteLine($"STILL seq/rev={still.Seq} {still.Width}x{still.Height} bytes={still.Bytes.Length}");
    File.WriteAllBytes(Path.Combine(outDir, "still.jpg"), still.Bytes);

    await Send(new { type = "mouse", action = "down", x = 400, y = 300, button = "left", clickCount = 1 });
    await Send(new { type = "mouse", action = "up", x = 400, y = 300, button = "left", clickCount = 1 });

    Console.WriteLine("E2E PASS");
}
finally
{
    try { if (!webui.HasExited) webui.Kill(entireProcessTree: true); } catch { }
    try { await webui.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token); } catch { }
    log.Dispose();
    Console.WriteLine($"logs/screens: {outDir}");
}

sealed class Session(ClientWebSocket ws)
{
    private readonly object _gate = new();
    private readonly List<Frame> _frames = [];
    private readonly List<StateMsg> _states = [];
    private Task? _loop;

    public long LastFlowSeq { get { lock (_gate) return _frames.LastOrDefault(f => f.Kind == 1)?.Seq ?? 0; } }

    public Task RunAsync() => _loop ??= Task.Run(LoopAsync);
    public void DumpStates() { lock (_gate) { foreach (var s in _states.TakeLast(5)) Console.WriteLine($"  state vw={s.ViewportWidth} vh={s.ViewportHeight} dpr={s.Dpr} fs={s.FrameScale} mode={s.Mode}"); } }

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
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t) && t.GetString() == "state")
                {
                    var s = new StateMsg(
                        root.TryGetProperty("viewportWidth", out var vw) ? vw.GetInt32() : 0,
                        root.TryGetProperty("viewportHeight", out var vh) ? vh.GetInt32() : 0,
                        root.TryGetProperty("dpr", out var d) ? d.GetDouble() : 0,
                        root.TryGetProperty("frameScale", out var fs) ? fs.GetDouble() : 0,
                        root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "");
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
sealed record StateMsg(int ViewportWidth, int ViewportHeight, double Dpr, double FrameScale, string Mode);



