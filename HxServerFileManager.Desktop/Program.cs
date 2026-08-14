using Photino.NET;

// 桌面壳：后台启动 Kestrel Web 服务，前台弹 Photino WebView 窗口指向 localhost。
// 与独立运行（dotnet run --project HxServerFileManager）共用 WebHost.Build 构建逻辑。
//
// 注意：入口方法不能是 async（Photino 的消息循环必须在同步主线程上运行，
//   否则窗口空白 —— 见 https://github.com/tryphotino/photino.NET/issues/180）。
//   top-level statement 中一旦出现 await，编译器就生成 async Task Main，
//   所以这里全部用同步阻塞调用。

// 随机选一个可用端口，避免与独立运行的实例冲突
var port = FindFreePort();
Environment.SetEnvironmentVariable("PORT", port.ToString());

var app = WebHost.Build(args);

// 后台启动 Web 服务（不阻塞主线程，Photino 窗口需要主线程消息循环）
_ = app.RunAsync();

// 同步等待 Kestrel 就绪再开窗口（不能用 await）
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

// 弹 Photino 窗口（WaitForClose 阻塞主线程，运行 WebView 消息循环）
var window = new PhotinoWindow()
    .SetTitle("HxServerFileManager")
    .SetUseOsDefaultSize(false)
    .SetSize(1280, 800)
    .Center()
    .SetDevToolsEnabled(true)
    .RegisterWebMessageReceivedHandler((sender, message) => { })
    .RegisterWindowClosingHandler((sender, e) =>
    {
        _ = app.StopAsync();
        return false; // false = 允许关闭
    })
    .Load(url);

window.WaitForClose();

static int FindFreePort()
{
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
