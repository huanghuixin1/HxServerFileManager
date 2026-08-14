using Photino.NET;

// 桌面壳：后台启动 Kestrel Web 服务，前台弹 Photino WebView 窗口指向 localhost。
// 与独立运行（dotnet run --project HxServerFileManager）共用 WebHost.Build 构建逻辑。

// 随机选一个可用端口，避免与独立运行的实例冲突
var port = FindFreePort();
Environment.SetEnvironmentVariable("PORT", port.ToString());

var app = WebHost.Build(args);

// 后台启动 Web 服务（不阻塞主线程，Photino 窗口需要主线程消息循环）
_ = app.RunAsync();

// 等待 Kestrel 就绪再开窗口（最多等 5 秒）
for (var i = 0; i < 50; i++)
{
    using var http = new HttpClient();
    try
    {
        var resp = await http.GetAsync($"http://localhost:{port}/api/health", new CancellationTokenSource(2000).Token);
        if (resp.IsSuccessStatusCode) break;
    }
    catch { /* 还没就绪，继续等 */ }
    await Task.Delay(100);
}

// 弹 Photino 窗口
var window = new PhotinoWindow()
    .SetTitle("HxServerFileManager")
    .SetUseOsDefaultSize(false)
    .SetSize(1280, 800)
    .Center()
    .Load($"http://localhost:{port}")
    .RegisterWebMessageReceivedHandler((sender, message) =>
    {
        // 预留：前端可通过 window.Photino.sendMessage(...) 与原生层通信
    });

// 窗口关闭时退出整个进程（Kestrel 也会随之停止）
window.RegisterWindowClosingHandler((sender, e) =>
{
    _ = app.StopAsync();
    return false; // false = 允许关闭；true = 阻止关闭
});

window.WaitForClose();

static int FindFreePort()
{
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
