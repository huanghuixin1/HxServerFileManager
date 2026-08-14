using Photino.NET;

// 桌面壳：后台启动 Kestrel Web 服务，前台弹 Photino WebView 窗口指向 localhost。
// 与独立运行（dotnet run --project HxServerFileManager）共用 WebHost.Build 构建逻辑。
//
// 三个坑（都是实测踩出来的，不要回退）：
// 1) 入口方法不能是 async —— Photino 的消息循环必须同步运行，
//    top-level statement 里出现 await 就会生成 async Task Main，窗口白屏。
// 2) Photino 窗口必须开在 STA 线程 —— .NET 主线程默认 MTA，
//    WebView2 在 MTA 线程上初始化直接失败（0x80010106 RPC_E_CHANGED_MODE），
//    且 Photino 静默吞掉该错误，表现为窗口白屏、无任何 WebView2 子进程。
// 3) Photino.NET 必须用 4.x —— 3.0.14 在 Windows 上建出的窗口失效（只剩标题栏）。

// 随机选一个可用端口，避免与独立运行的实例冲突
var port = FindFreePort();
Environment.SetEnvironmentVariable("PORT", port.ToString());

var app = WebHost.Build(args);

// 后台启动 Web 服务（不阻塞，Photino 窗口需要自己的 STA 线程消息循环）
_ = app.RunAsync();

// 同步等待 Kestrel 就绪再开窗口（不能用 await，见上方坑 1）
var url = $"http://localhost:{port}";
for (var i = 0; i < 50; i++)
{
    try
    {
        using var http = new HttpClient();
        var resp = http.GetAsync(url + "/api/health", CancellationToken.None).Result;
        if (resp.IsSuccessStatusCode) break;
    }
    catch { /* 还没就绪 */ }
    Thread.Sleep(100);
}

// 在 STA 线程上创建并运行 Photino 窗口（见上方坑 2；WaitForClose 阻塞该线程跑消息循环）
var uiThread = new Thread(() =>
{
    var window = new PhotinoWindow()
        .SetTitle("HxServerFileManager")
        .SetUseOsDefaultSize(false)
        .SetSize(1280, 800)
        .Center()
        .SetDevToolsEnabled(true)
        .RegisterWindowClosingHandler((sender, e) =>
        {
            _ = app.StopAsync();
            return false; // false = 允许关闭
        })
        .Load(url);

    window.WaitForClose();
});
if (OperatingSystem.IsWindows())
    uiThread.SetApartmentState(ApartmentState.STA); // Windows 上 WebView2 只能在 STA 线程创建
uiThread.Start();
uiThread.Join();

// 窗口关闭后停掉 Kestrel 再退出
app.StopAsync().GetAwaiter().GetResult();

static int FindFreePort()
{
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
