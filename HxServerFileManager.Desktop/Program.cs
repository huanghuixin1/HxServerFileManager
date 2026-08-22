using System.Formats.Tar;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Photino.NET;

// 桌面壳：后台启动 Kestrel Web 服务，前台弹 Photino WebView 窗口指向 localhost。
// 与独立运行（dotnet run --project HxServerFileManager）共用 WebHost.Build 构建逻辑。
//
// 三个坑（都是实测踩出来的，不要回退）：
// 1) 入口方法不能是 async —— Photino 的消息循环必须同步运行，
//    top-level statement 里出现 await 就会生成 async Task Main，窗口白屏。
// 2) 窗口创建线程分平台：Windows 必须 STA 线程（WebView2 在 MTA 线程上初始化直接失败
//    0x80010106 RPC_E_CHANGED_MODE，且主线程默认 MTA、启动后不可改公寓状态）；
//    macOS 必须在主线程（AppKit 在主线程外建 NSWindow 直接抛
//    "NSWindow drag regions should only be invalidated on the Main Thread!"）；
//    Linux（GTK/WebKitGTK）同 macOS，也用主线程。
// 3) Photino.NET 必须用 4.x —— 3.0.14 在 Windows 上建出的窗口失效（只剩标题栏）。

// 随机选一个可用端口，避免与独立运行的实例冲突
var port = FindFreePort();
Environment.SetEnvironmentVariable("PORT", port.ToString());

// 桌面壳忽略上传大小限制：0 = 不限制。即使 configs/env.json 配了 maxUploadMb 也不生效，
// 本地窗口直连 localhost，没必要卡单文件大小（WebHost.Build 读取时本环境变量优先）
Environment.SetEnvironmentVariable("HXSFM_MAX_UPLOAD_MB", "0");

// 标记桌面环境：/api/health 返回 desktop=true，前端据此走原生保存对话框等桌面专属交互
Environment.SetEnvironmentVariable("HXSFM_DESKTOP", "1");

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

// 窗口图标（logo.png 内嵌资源 → 临时文件），先解出一次，窗口与 Linux 自装共用
var logoPath = TryExtractLogoPng();

// Linux：把图标装进用户图标主题并生成应用菜单项，保证「拷贝文件夹启动后也有图标」。
// 原因：.desktop 的 Icon= 只认图标主题名或绝对路径，相对路径（Icon=logo.png）解析不了。
// 放在开窗前执行，首次启动任务栏/应用菜单立即有 logo。
if (OperatingSystem.IsLinux())
    InstallLinuxIcon(logoPath);

// 创建并运行 Photino 窗口（WaitForClose 阻塞当前线程跑消息循环）。
// 线程策略：Windows 新开 STA 线程（WebView2 要求，主线程默认 MTA 且不可改）；
// macOS / Linux 必须在主线程上直接调用（AppKit / GTK 只允许主线程建窗口，见上方坑 2）。
static void ShowWindow(WebApplication app, string url, string? logoPath)
{
    var window = new PhotinoWindow()
        // 标题带版本号：版本从主项目程序集读取（WebHost.AppVersion），与 HX 独立运行的版本一致
        .SetTitle("彗星ssh v" + WebHost.AppVersion())
        .SetUseOsDefaultSize(false)
        .SetSize(1280, 800)
        .Center()
        .SetDevToolsEnabled(true)
        // 关掉 WebView 原生右键菜单（含「刷新」）：误点刷新会把整个 SPA 重载、断掉所有 SSH 会话。
        // 只禁原生菜单——DOM 的 contextmenu 事件照常触发，前端自己的右键行为（终端右键粘贴）不受影响
        .SetContextMenuEnabled(false)
        .RegisterWindowClosingHandler((sender, e) =>
        {
            AppState.BeginShutdown(); // 标记窗口开始关闭：取消在途下载、禁止再回传消息（防关闭瞬间 SendWebMessage 原生崩溃闪退）
            _ = app.StopAsync();
            return false; // false = 允许关闭
        })
        // JS↔C# 消息桥：前端在桌面壳里发 { op, ... } JSON（window.external.sendMessage），
        // 这里处理 op=saveFile（导出连接时弹原生「另存为」对话框选路径并写文件），结果经 SendWebMessage 回给前端
        .RegisterWebMessageReceivedHandler((sender, message) =>
        {
            var window = (PhotinoWindow)sender!; // 消息桥回调的 sender 必为 PhotinoWindow
            DesktopRequest? req = null;
            try
            {
                req = JsonSerializer.Deserialize<DesktopRequest>(message, DesktopBridge.JsonOpts);
                if (req?.Op == "saveFile")
                {
                    // 用跨平台对话框（Windows=Win32 GetSaveFileName 可预填默认文件名、绕开 Photino
                    // 4.0.16 在 Windows 上的 HRESULT 覆盖 bug；macOS/Linux=Photino 原生对话框）。
                    var path = Dialogs.SaveFileDialog(
                        window,
                        "导出连接",
                        req.DefaultName ?? "hxsfm-connections.json",
                        [("JSON 文件 (*.json)", ["*.json"]), ("所有文件 (*.*)", ["*.*"])]);
                    if (path != null)
                    {
                        // 后缀校验：用户没输入 .json 结尾时补上，保证导出文件扩展名一致
                        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            path += ".json";
                        File.WriteAllText(path, req.Content ?? "");
                        DesktopBridge.SendResponse(window, new { op = "saveFileResult", ok = true, path });
                    }
                    else
                    {
                        DesktopBridge.SendResponse(window, new { op = "saveFileResult", ok = false, canceled = true });
                    }
                }
                else if (req?.Op == "downloadMany")
                {
                    // 批量下载（多选文件/文件夹）：选一个本地文件夹，远端 tar 流解包到该目录（保留目录结构）
                    var folder = Dialogs.PickFolderDialog(window, "选择下载保存文件夹");
                    if (folder == null)
                    {
                        DesktopBridge.SendResponse(window, new { op = "downloadManyResult", id = req.Id, ok = false, canceled = true });
                    }
                    else
                    {
                        var targetDir = folder;
                        var url = req.Url;
                        var paths = req.Paths ?? [];
                        var id = req.Id;
                        // 独立取消源：用户点「停止下载」只取消本次任务；窗口关闭仍由 ShutdownToken 统一取消
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(AppState.ShutdownToken);
                        if (!string.IsNullOrEmpty(id)) ActiveDownloads.Map[id] = cts;
                        _ = Task.Run(async () =>
                        {
                            var created = new List<string>(); // 本次解包创建的文件/目录（取消/失败时清理半成品）
                            try
                            {
                                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                                var body = JsonSerializer.Serialize(new { paths }, DesktopBridge.JsonOpts);
                                // ResponseHeadersRead：拿到头就返回，流式读取响应体 —— PostAsync 默认会把
                                // 整个 tar 缓冲进内存（ResponseContentRead），大目录直接爆内存
                                using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
                                {
                                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                                };
                                using var resp = await client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                                resp.EnsureSuccessStatusCode();
                                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                                await using var tar = new TarReader(stream, leaveOpen: false);
                                var done = 0;
                                var lastSent = DateTime.UtcNow;
                                TarEntry? entry;
                                while ((entry = await tar.GetNextEntryAsync(copyData: false, cts.Token)) != null)
                                {
                                    // 防路径逃逸：只解相对路径、不含 ..、非绝对路径
                                    var rel = entry.Name.Replace('\\', '/').TrimStart('/');
                                    if (rel.Length == 0 || rel == ".." || rel.StartsWith("../", StringComparison.Ordinal)
                                        || Path.IsPathRooted(rel))
                                        continue;
                                    var dest = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));
                                    if (entry.EntryType == TarEntryType.Directory)
                                    {
                                        Directory.CreateDirectory(dest);
                                        created.Add(dest);
                                    }
                                    else if (entry.EntryType == TarEntryType.RegularFile)
                                    {
                                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                                        created.Add(dest); // 先登记再解包：取消/失败时把写了一半的文件也清掉
                                        await entry.ExtractToFileAsync(dest, overwrite: true, cts.Token);
                                    }
                                    else
                                    {
                                        continue; // 符号链接等跳过，避免意外
                                    }
                                    done++;
                                    // 进度节流：最长 80ms 回一次，避免海量小文件刷爆消息桥
                                    if (DateTime.UtcNow - lastSent > TimeSpan.FromMilliseconds(80))
                                    {
                                        lastSent = DateTime.UtcNow;
                                        DesktopBridge.SendResponse(window, new { op = "downloadManyProgress", done, file = entry.Name });
                                    }
                                }
                                DesktopBridge.SendResponse(window, new { op = "downloadManyResult", id, ok = true, path = targetDir, count = done });
                            }
                            catch (OperationCanceledException)
                            {
                                if (AppState.WindowClosing) return; // 窗口关闭/应用退出：不发消息（窗口可能已销毁）
                                // 用户点「停止下载」：清理已解包的部分文件，回 canceled 让前端提示「已取消」
                                CleanupPartial(created);
                                DesktopBridge.SendResponse(window, new { op = "downloadManyResult", id, ok = false, canceled = true });
                            }
                            catch (Exception ex)
                            {
                                CleanupPartial(created);
                                DesktopBridge.SendResponse(window, new
                                {
                                    op = "downloadManyResult",
                                    id,
                                    ok = false,
                                    error = ex.Message,
                                    // 流传输失败时真正的根因在 InnerException（如 response ended prematurely），
                                    // 一并回传让前端显示可诊断的信息，而不是只有外层包装消息
                                    innerError = ex.InnerException?.Message,
                                });
                            }
                            finally
                            {
                                if (!string.IsNullOrEmpty(id)) ActiveDownloads.Map.TryRemove(id, out _);
                            }
                        });
                    }
                }
                else if (req?.Op == "downloadManyCancel")
                {
                    // 用户点「停止下载」：取消对应在途任务（取消 HttpClient 请求 → 服务端 RequestAborted
                    // → 远端 tar 终止 → 本地清理已解包的部分文件）
                    if (!string.IsNullOrEmpty(req.Id) && ActiveDownloads.Map.TryGetValue(req.Id, out var cts))
                        cts.Cancel();
                }
                else if (req?.Op == "downloadFile")
                {
                    // 弹原生「另存为」对话框选保存位置，默认文件名 = 远端文件名（用户要求）
                    var srcExt = Path.GetExtension(req.DefaultName ?? "");
                    var path = Dialogs.SaveFileDialog(
                        window,
                        "下载文件",
                        req.DefaultName ?? "download",
                        [("所有文件 (*.*)", ["*.*"])]);
                    if (path == null)
                    {
                        DesktopBridge.SendResponse(window, new { op = "downloadFileResult", ok = false, canceled = true });
                    }
                    else
                    {
                        // 用户把扩展名删掉时按原文件名补回（如 report.txt 输成 backup → backup.txt）
                        if (!string.IsNullOrEmpty(srcExt) && !Path.HasExtension(path))
                            path += srcExt;
                        var target = path;
                        // 后台流式下载落盘（大文件不阻塞 UI 线程），完成后回传结果。
                        // 不设超时：大文件/慢速远端可能要很久（服务端已关 Kestrel 响应速率限制并全程 Touch
                        // 防会话回收，见 Program.cs /api/download）；窗口关闭时经 ShutdownToken 取消，避免
                        // 在已销毁的窗口上 SendWebMessage 导致原生崩溃闪退。
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                                using var resp = await client.GetAsync(req.Url ?? "", HttpCompletionOption.ResponseHeadersRead, AppState.ShutdownToken);
                                resp.EnsureSuccessStatusCode();
                                await using var fs = File.Create(target);
                                await using var stream = await resp.Content.ReadAsStreamAsync();
                                await stream.CopyToAsync(fs, AppState.ShutdownToken);
                                DesktopBridge.SendResponse(window, new { op = "downloadFileResult", ok = true, path = target });
                            }
                            catch (OperationCanceledException)
                            {
                                // 窗口关闭/应用退出触发的取消：不发消息（窗口可能已销毁）
                            }
                            catch (Exception ex)
                            {
                                DesktopBridge.SendResponse(window, new
                                {
                                    op = "downloadFileResult",
                                    ok = false,
                                    error = ex.Message,
                                    innerError = ex.InnerException?.Message,
                                });
                            }
                        });
                    }
                }
                else if (req?.Op == "uploadDropped")
                {
                    // Linux 拖拽上传：前端收到 desktopDrop（本地文件/文件夹路径列表）后回传连接信息，
                    // 这里在后台把本地文件经本地 Kestrel /api/ensure-dirs + /api/upload 传到远端。
                    // 浏览器 JS 无法读取任意本地路径，由壳进程代读代传，前端只显示进度面板。
                    var paths = req.Paths ?? [];
                    var connId = req.ConnId;
                    var dir = req.Dir;
                    var token = req.Token;
                    var baseUrl = string.IsNullOrEmpty(req.BaseUrl) ? url.TrimEnd('/') : req.BaseUrl.TrimEnd('/');
                    var id = req.Id;
                    if (string.IsNullOrEmpty(connId) || paths.Length == 0)
                    {
                        DropLog.Log($"uploadDropped: missing connId={connId} paths={paths.Length}");
                        DesktopBridge.SendResponse(window, new { op = "uploadDroppedResult", id, ok = false, error = "缺少连接或拖入的内容" });
                    }
                    else
                    {
                        // 校验后固化非空值（Task.Run lambda 不继承外层流分析，直接引用可空变量会出 CS8604 警告）
                        var conn = connId;
                        var baseDir = dir ?? "/";
                        var authToken = token ?? "";
                        DropLog.Log($"uploadDropped: conn={conn} dir={baseDir} paths={paths.Length}");
                        // 独立取消源：用户点「停止上传」只取消本次任务；窗口关闭仍由 ShutdownToken 统一取消
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(AppState.ShutdownToken);
                        if (!string.IsNullOrEmpty(id)) ActiveUploads.Map[id] = cts;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // 收集本地目录树：dirs=相对目录（父级在前、含空目录）、files=(绝对路径, 相对目录)
                                var dirs = new List<string>();
                                var files = new List<(string Abs, string RelDir)>();
                                foreach (var p in paths)
                                {
                                    try
                                    {
                                        if (File.Exists(p)) files.Add((p, ""));
                                        else if (Directory.Exists(p))
                                            CollectLocalTree(Path.GetFullPath(p), Path.GetFileName(p.TrimEnd('/', '\\')), dirs, files);
                                    }
                                    catch { /* 单个路径不可读则跳过 */ }
                                }
                                if (files.Count == 0 && dirs.Count == 0)
                                {
                                    DropLog.Log("uploadDropped: nothing readable collected");
                                    DesktopBridge.SendResponse(window, new { op = "uploadDroppedResult", id, ok = false, error = "拖入的内容不是可读的文件/文件夹" });
                                    return;
                                }
                                DropLog.Log($"uploadDropped: dirs={dirs.Count} files={files.Count}");
                                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                                // 1) 先批量建目录（父级在前，含空目录；已存在则后端跳过）
                                if (dirs.Count > 0)
                                {
                                    var body = JsonSerializer.Serialize(new { connectionId = conn, path = baseDir, dirs }, DesktopBridge.JsonOpts);
                                    using var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/ensure-dirs")
                                    {
                                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                                    };
                                    if (!string.IsNullOrEmpty(authToken)) reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
                                    using var resp = await client.SendAsync(reqMsg, cts.Token);
                                    resp.EnsureSuccessStatusCode();
                                }
                                // 2) 逐文件上传（目标目录 = 前端 cwd + 相对目录）
                                var total = files.Count;
                                var done = 0;
                                foreach (var (abs, rel) in files)
                                {
                                    done++;
                                    var relName = string.IsNullOrEmpty(rel) ? Path.GetFileName(abs) : rel + "/" + Path.GetFileName(abs);
                                    DesktopBridge.SendResponse(window, new
                                    {
                                        op = "uploadDroppedProgress",
                                        id,
                                        index = done,
                                        total,
                                        name = relName,
                                        percent = total > 0 ? (int)Math.Round(done * 100.0 / total) : 100,
                                    });
                                    var targetDir = string.IsNullOrEmpty(rel) ? baseDir : (baseDir == "/" ? "/" + rel : baseDir.TrimEnd('/') + "/" + rel);
                                    await using var fs = File.OpenRead(abs);
                                    using var form = new MultipartFormDataContent();
                                    form.Add(new StringContent(conn), "connId");
                                    form.Add(new StringContent(targetDir), "path");
                                    var fileContent = new StreamContent(fs);
                                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                                    form.Add(fileContent, "file", Path.GetFileName(abs));
                                    using var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/upload") { Content = form };
                                    if (!string.IsNullOrEmpty(authToken)) reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
                                    using var resp = await client.SendAsync(reqMsg, cts.Token);
                                    resp.EnsureSuccessStatusCode();
                                }
                                DropLog.Log($"uploadDropped: done {total} files");
                                DesktopBridge.SendResponse(window, new { op = "uploadDroppedResult", id, ok = true, count = total });
                            }
                            catch (OperationCanceledException)
                            {
                                if (AppState.WindowClosing) return; // 窗口关闭/应用退出：不发消息（窗口可能已销毁）
                                DropLog.Log("uploadDropped: canceled");
                                DesktopBridge.SendResponse(window, new { op = "uploadDroppedResult", id, ok = false, canceled = true });
                            }
                            catch (Exception ex)
                            {
                                DropLog.Log($"uploadDropped error: {ex.Message}");
                                DesktopBridge.SendResponse(window, new { op = "uploadDroppedResult", id, ok = false, error = ex.Message });
                            }
                            finally
                            {
                                if (!string.IsNullOrEmpty(id)) ActiveUploads.Map.TryRemove(id, out _);
                            }
                        });
                    }
                }
                else if (req?.Op == "uploadDroppedCancel")
                {
                    // 用户点「停止上传」：取消对应在途任务（取消 HttpClient 请求 → 本次上传中断）
                    if (!string.IsNullOrEmpty(req.Id) && ActiveUploads.Map.TryGetValue(req.Id, out var cts))
                        cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                // 按实际 op 回对应 Result，避免前端对应的一次性回调永远收不到而挂起
                var op = req?.Op switch
                {
                    "saveFile" => "saveFileResult",
                    "downloadFile" => "downloadFileResult",
                    "downloadMany" => "downloadManyResult",
                    "uploadDropped" => "uploadDroppedResult",
                    _ => "saveFileResult",
                };
                DesktopBridge.SendResponse(window, new { op, ok = false, error = ex.Message });
            }
        })
        .Load(url);

    // 窗口图标：用 PNG 而非 ICO —— Linux(gdk-pixbuf) 对现代多尺寸 ICO 支持很差（会显示成噪点）。
    // 解出/加载失败时静默忽略，不阻断窗口启动。
    if (logoPath != null)
    {
        try { window.SetIconFile(logoPath); }
        catch { /* 图标加载失败不影响使用 */ }
    }

    // Linux：接管 GTK 拖放（WebKitGTK 外部文件拖放有 bug，DOM 事件不触发，见 GtkDrop 注释）。
    // webview 在 WaitForClose → Photino_ctor 里才建好，这里用 idle 等主循环跑起来再挂信号。
    if (OperatingSystem.IsLinux())
        GtkDrop.ScheduleInstall(window);

    window.WaitForClose();
}

if (OperatingSystem.IsWindows())
{
    // WebView2 只能在 STA 线程创建（主线程默认 MTA、启动后不可改公寓状态），新开线程并设 STA
    var uiThread = new Thread(() => ShowWindow(app, url, logoPath));
    uiThread.SetApartmentState(ApartmentState.STA); // Windows 上 WebView2 只能在 STA 线程创建
    uiThread.Start();
    uiThread.Join();
}
else
{
    // macOS / Linux：AppKit / GTK 只允许主线程操作 UI，必须在进程主线程上直接创建窗口
    ShowWindow(app, url, logoPath);
}

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

// 从内嵌资源解出 logo.png 到临时目录，返回路径；失败返回 null（不阻断启动）
static string? TryExtractLogoPng()
{
    try
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase));
        if (resName == null) return null;
        using var res = asm.GetManifestResourceStream(resName);
        if (res == null) return null;
        var tmpDir = Path.Combine(Path.GetTempPath(), "HxServerFileManager");
        Directory.CreateDirectory(tmpDir);
        var iconPath = Path.Combine(tmpDir, "logo.png");
        using (var fs = File.Create(iconPath)) res.CopyTo(fs);
        return iconPath;
    }
    catch { return null; }
}

// Linux：把 logo 装入用户图标主题（~/.local/share/icons/hicolor/*/apps/hxsfm.png），
// 并生成 ~/.local/share/applications/hxsfm.desktop（绝对路径 Exec + 主题图标名 Icon=hxsfm）。
// 这样应用菜单、任务栏都有图标；文件夹拷到任意位置都生效，无需手动安装。失败静默忽略。
static void InstallLinuxIcon(string? iconPath)
{
    try
    {
        if (iconPath == null || !File.Exists(iconPath)) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return;

        var iconRoot = Path.Combine(home, ".local", "share", "icons", "hicolor");
        foreach (var s in new[] { 256, 128, 64, 48, 32 })
        {
            var dir = Path.Combine(iconRoot, $"{s}x{s}", "apps");
            Directory.CreateDirectory(dir);
            File.Copy(iconPath, Path.Combine(dir, "hxsfm.png"), overwrite: true);
        }
        try // 刷新图标缓存（无此命令/失败都忽略）
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gtk-update-icon-cache")
            {
                Arguments = "-f -t \"" + iconRoot + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }

        var appsDir = Path.Combine(home, ".local", "share", "applications");
        Directory.CreateDirectory(appsDir);
        var exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe)) return;
        var content = string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Version=1.0",
            "Name=HxServerFileManager",
            "GenericName=SSH File Manager",
            "Comment=基于 Kestrel + SSH.NET 的服务器文件管理 / WebSSH",
            $"Exec=\"{exe}\"",
            "Icon=hxsfm",
            "StartupWMClass=HxServerFileManager.Desktop",
            "Terminal=false",
            "Categories=Network;FileManager;Development;",
            "StartupNotify=false",
            "");
        File.WriteAllText(Path.Combine(appsDir, "hxsfm.desktop"), content);
    }
    catch { /* 自装图标失败不影响使用 */ }
}

// 递归收集本地目录树：dirs=相对路径目录（父级在前，含空目录）、files=(绝对路径, 相对目录)
// rel 为相对当前目录的子路径（'' = 直接放当前目录），与前端 collectEntries 语义一致
static void CollectLocalTree(string abs, string rel, List<string> dirs, List<(string Abs, string RelDir)> files)
{
    dirs.Add(rel);
    foreach (var d in Directory.EnumerateDirectories(abs))
        CollectLocalTree(d, rel + "/" + Path.GetFileName(d), dirs, files);
    foreach (var f in Directory.EnumerateFiles(abs))
        files.Add((f, rel));
}

// 清理批量下载中途取消/失败时已解包的部分文件：倒序删除（子项先删，目录空了才删）。
// 只删本次任务创建的内容，不动用户目录里原本就有的文件；best-effort，失败静默跳过。
static void CleanupPartial(List<string> created)
{
    for (var i = created.Count - 1; i >= 0; i--)
    {
        try
        {
            var p = created[i];
            if (Directory.Exists(p))
            {
                if (!Directory.EnumerateFileSystemEntries(p).Any())
                    Directory.Delete(p);
            }
            else if (File.Exists(p))
            {
                File.Delete(p);
            }
        }
        catch { /* 清理失败不阻断主流程 */ }
    }
}

// ---- 在途批量下载注册表：前端发 downloadMany 时登记取消源，downloadManyCancel 时取消对应任务 ----
static class ActiveDownloads
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> Map = new();
}

// ---- 在途拖拽上传注册表：uploadDropped 登记取消源，uploadDroppedCancel 取消对应任务 ----
static class ActiveUploads
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> Map = new();
}

// ---- 拖放诊断日志（排查 Linux 拖拽问题时看临时目录 drop-debug.log；日志失败绝不影响主流程）----
static class DropLog
{
    private static readonly object Lock = new();
    public static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "HxServerFileManager");
            Directory.CreateDirectory(dir);
            lock (Lock)
                File.AppendAllText(Path.Combine(dir, "drop-debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
        }
        catch { }
    }
}

// ---- Linux GTK3 拖放接管（绕开 WebKitGTK 外部文件拖放 bug）----
// WebKitGTK 对「从系统文件管理器拖文件进页面」的 HTML5 drag/drop 事件支持存在多个未修复 bug
// （webkit bug 204281 / 198915 / 320301；Photino.Native issue #152），实测 Linux 上 dragenter/drop
// 根本不触发。这里直接 P/Invoke GTK3 在 webview 上接管拖放：
// 用 g_signal_connect（普通优先级，先于 WebKit 内部的 connect_after）注册 drag-motion/drag-drop，
// 命中 text/uri-list 时 return TRUE 抢占处理（GTK 的 true_handled 累积器会停止后续 handler，
// WebKit 的 after-handler 不再运行，其内部 DropTarget 因从未收到 drag-motion 也不会再介入），
// 取回本地文件路径后经消息桥发 {op:'desktopDrop', paths}，前端再回传连接信息完成上传。
static class GtkDrop
{
    public static PhotinoWindow? Window; // 唯一窗口，拖放信号回调里用它回传消息

    const int GDK_ACTION_COPY = 2;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool GtkDragMotionFn(IntPtr widget, IntPtr context, int x, int y, uint time, IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void GtkDragLeaveFn(IntPtr widget, IntPtr context, uint time, IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool GtkDragDropFn(IntPtr widget, IntPtr context, int x, int y, uint time, IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void GtkDragDataReceivedFn(IntPtr widget, IntPtr context, int x, int y, IntPtr data, uint info, uint time, IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool GSourceFn(IntPtr userData);

    // 委托必须保持引用防 GC（否则信号触发时回调地址已失效直接崩）
    static readonly GtkDragMotionFn MotionFn = OnDragMotion;
    static readonly GtkDragLeaveFn LeaveFn = OnDragLeave;
    static readonly GtkDragDropFn DropFn = OnDragDrop;
    static readonly GtkDragDataReceivedFn DataReceivedFn = OnDragDataReceived;
    static readonly GSourceFn InstallIdleFn = OnInstallIdle;

    static readonly IntPtr UriListAtom = gdk_atom_intern_static_string("text/uri-list");

    static bool _dragActive; // 是否正在接管一个文件拖拽
    static IntPtr _dragContext; // 当前接管拖拽的 GdkDragContext（换 context 才重新取数）
    static bool _awaitingDropData; // drop 阶段在等数据（motion 阶段没取到、drop 时再取一次的兜底）
    static readonly List<string> _pendingPaths = new(); // 已取回的本地路径缓存
    static int _installAttempts;
    static bool _installed;

    // 窗口创建后调度安装：gdk_threads_add_idle 等 GTK 主循环跑起来再挂信号
    // （webview 在 WaitForClose → Photino_ctor → Photino::Show 时才建好，主线程正阻塞在里面）
    public static void ScheduleInstall(PhotinoWindow window)
    {
        Window = window;
        DropLog.Log($"schedule install (OS={Environment.OSVersion.Platform})");
        try { gdk_threads_add_idle(InstallIdleFn, IntPtr.Zero); } catch (Exception ex) { DropLog.Log($"gdk_threads_add_idle threw {ex.Message}"); }
    }

    static bool OnInstallIdle(IntPtr data)
    {
        try { Install(); }
        catch (Exception ex) { DropLog.Log($"install threw {ex.Message}"); }
        if (!_installed && ++_installAttempts < 30)
            gdk_threads_add_idle(InstallIdleFn, IntPtr.Zero); // 窗口可能还没建好，稍后重试
        return false;
    }

    static void Install()
    {
        if (_installed) return;
        // 找 Photino 主窗口里的 WebKitWebView：顶层窗口（启动时只有主窗口）的 bin child 即 webview
        var toplevels = gtk_window_list_toplevels();
        var count = 0;
        for (var node = toplevels; node != IntPtr.Zero; node = NextList(node))
        {
            count++;
            var win = ListData(node);
            if (win == IntPtr.Zero) continue;
            var child = gtk_bin_get_child(win);
            if (child == IntPtr.Zero) continue;
            var h1 = g_signal_connect_data(child, "drag-motion", MotionFn, IntPtr.Zero, IntPtr.Zero, 0);
            var h2 = g_signal_connect_data(child, "drag-leave", LeaveFn, IntPtr.Zero, IntPtr.Zero, 0);
            var h3 = g_signal_connect_data(child, "drag-drop", DropFn, IntPtr.Zero, IntPtr.Zero, 0);
            var h4 = g_signal_connect_data(child, "drag-data-received", DataReceivedFn, IntPtr.Zero, IntPtr.Zero, 0);
            DropLog.Log($"install: toplevel#{count} win={win} child={child} handlers={h1},{h2},{h3},{h4}");
            // 只要尝试过连接就标记完成（重试会重复挂信号导致 desktopDrop 重复触发）；
            // handler id 为 0（连接失败）的情况由日志暴露，正常启动这里都是非 0
            _installed = true;
        }
        if (count == 0) DropLog.Log("install: no toplevels yet, will retry");
    }

    static bool OnDragMotion(IntPtr widget, IntPtr context, int x, int y, uint time, IntPtr userData)
    {
        if (!HasUriTarget(context))
        {
            DropLog.Log($"drag-motion({context}): no uri target, hand back to WebKit");
            return false; // 非文件拖拽：交还 WebKit 默认处理
        }
        gdk_drag_status(context, GDK_ACTION_COPY, time);
        if (!_dragActive || _dragContext != context)
        {
            DropLog.Log($"drag-motion({context}): takeover");
            _dragActive = true;
            _dragContext = context;
            _pendingPaths.Clear();
            Send(new { op = "desktopDragState", active = true }); // 前端浮出「松开以上传」遮罩
            // 进入即取数据（WebKit DropTarget 同款模式：motion 阶段取回、drop 用缓存，兼容性最好）
            try { gtk_drag_get_data(widget, context, UriListAtom, time); }
            catch (Exception ex) { DropLog.Log($"drag-motion: gtk_drag_get_data threw {ex.Message}"); }
        }
        return true; // 抢占：停止后续 handler（含 WebKit 的 connect_after）
    }

    static void OnDragLeave(IntPtr widget, IntPtr context, uint time, IntPtr userData)
    {
        if (!_dragActive) return;
        DropLog.Log($"drag-leave({context})");
        _dragActive = false;
        _dragContext = IntPtr.Zero;
        _awaitingDropData = false;
        _pendingPaths.Clear();
        Send(new { op = "desktopDragState", active = false });
    }

    static bool OnDragDrop(IntPtr widget, IntPtr context, int x, int y, uint time, IntPtr userData)
    {
        if (!_dragActive) return false;
        DropLog.Log($"drag-drop({context}): cachedPaths={_pendingPaths.Count}");
        if (_pendingPaths.Count == 0)
        {
            // 兜底：motion 阶段没取到数据，drop 时再取一次（少数源只在放下时给数据）
            _awaitingDropData = true;
            try { gtk_drag_get_data(widget, context, UriListAtom, time); }
            catch (Exception ex)
            {
                DropLog.Log($"drag-drop: gtk_drag_get_data threw {ex.Message}");
                _awaitingDropData = false;
            }
            if (_awaitingDropData) return true; // 等 drag-data-received 收尾
        }
        CompleteDrop(context, time);
        return true;
    }

    static void OnDragDataReceived(IntPtr widget, IntPtr context, int x, int y, IntPtr data, uint info, uint time, IntPtr userData)
    {
        if (!_dragActive || context != _dragContext) return;
        var parsed = -1;
        try
        {
            if (gtk_selection_data_get_data_type(data) == UriListAtom)
            {
                var len = gtk_selection_data_get_length(data);
                parsed = len > 0 ? ParseUriList(gtk_selection_data_get_data(data), len).Count : 0;
                if (parsed > 0)
                {
                    _pendingPaths.Clear();
                    _pendingPaths.AddRange(ParseUriList(gtk_selection_data_get_data(data), gtk_selection_data_get_length(data)));
                }
            }
            DropLog.Log($"drag-data-received({context}): type={(gtk_selection_data_get_data_type(data) == UriListAtom ? "uri-list" : "other")} len={gtk_selection_data_get_length(data)} parsed={parsed}");
        }
        catch (Exception ex) { DropLog.Log($"drag-data-received: parse threw {ex.Message}"); }
        if (_awaitingDropData)
        {
            _awaitingDropData = false;
            CompleteDrop(context, time);
        }
    }

    static void CompleteDrop(IntPtr context, uint time)
    {
        _dragActive = false;
        _dragContext = IntPtr.Zero;
        Send(new { op = "desktopDragState", active = false }); // 收起遮罩
        var paths = _pendingPaths.ToArray();
        _pendingPaths.Clear();
        DropLog.Log($"drop complete({context}): {paths.Length} paths: {string.Join(" | ", paths)}");
        try { gtk_drag_finish(context, paths.Length > 0, false, time); } catch (Exception ex) { DropLog.Log($"gtk_drag_finish threw {ex.Message}"); }
        if (paths.Length > 0)
            Send(new { op = "desktopDrop", paths });
    }

    static bool HasUriTarget(IntPtr context)
    {
        try
        {
            var list = gdk_drag_context_list_targets(context);
            for (var node = list; node != IntPtr.Zero; node = NextList(node))
                if (ListData(node) == UriListAtom) return true;
        }
        catch { }
        return false;
    }

    // text/uri-list：多行 file:// URI，逐行解析为本地路径
    static List<string> ParseUriList(IntPtr data, int len)
    {
        var paths = new List<string>();
        var bytes = new byte[len];
        Marshal.Copy(data, bytes, 0, len);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var raw in text.Split('\n', '\r'))
        {
            var uri = raw.Trim();
            if (uri.Length == 0 || !uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) continue;
            try { paths.Add(new Uri(uri).LocalPath); } catch { }
        }
        return paths;
    }

    static void Send(object payload)
    {
        if (Window == null || AppState.WindowClosing) return;
        try { DesktopBridge.SendResponse(Window!, payload); } catch { }
    }

    // GList/GSList 同布局：data(0) / next(8)
    static IntPtr ListData(IntPtr node) => Marshal.ReadIntPtr(node);
    static IntPtr NextList(IntPtr node) => Marshal.ReadIntPtr(node, IntPtr.Size);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_window_list_toplevels();

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_bin_get_child(IntPtr bin);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_drag_get_data(IntPtr widget, IntPtr context, IntPtr target, uint time_);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_drag_finish(IntPtr context, bool success, bool del, uint time_);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_selection_data_get_data_type(IntPtr data);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int gtk_selection_data_get_length(IntPtr data);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_selection_data_get_data(IntPtr data);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gdk_drag_context_list_targets(IntPtr context);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gdk_drag_status(IntPtr context, int action, uint time_);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gdk_atom_intern_static_string([MarshalAs(UnmanagedType.LPUTF8Str)] string atomName);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint gdk_threads_add_idle(GSourceFn function, IntPtr data);

    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong g_signal_connect_data(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string detailedSignal, Delegate cHandler, IntPtr data, IntPtr destroyData, int connectFlags);
}

// ---- 桌面消息桥：把结果 JSON 回传给前端（前端用 window.external.receiveMessage 接收）----
static class DesktopBridge
{
    // 与前端约定的 JSON 选项：camelCase 字段 + 大小写不敏感（前端发来的字段是 camelCase）
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void SendResponse(PhotinoWindow window, object payload)
    {
        if (AppState.WindowClosing) return; // 窗口已开始关闭：目标可能已销毁，发送会原生崩溃
        try
        {
            window.SendWebMessage(JsonSerializer.Serialize(payload, JsonOpts));
        }
        catch { /* 关闭竞态：忽略 */ }
    }
}

// ---- 应用生命周期状态：窗口关闭时取消在途下载、禁止再回传消息（防关闭瞬间 SendWebMessage 原生崩溃闪退）----
static class AppState
{
    public static volatile bool WindowClosing;
    private static readonly CancellationTokenSource Cts = new();
    public static CancellationToken ShutdownToken => Cts.Token;
    public static void BeginShutdown()
    {
        WindowClosing = true;
        try { Cts.Cancel(); } catch { /* 已取消 */ }
    }
}

// ---- 跨平台「另存为/选文件夹」对话框 ----
// Windows 用 Win32 GetSaveFileName（能预填默认文件名、绕开 Photino 4.0.16 在 Windows 上
// SHCreateItemFromParsingName 覆盖 HRESULT 导致保存对话框不弹的 bug，见下）；
// macOS/Linux 用 Photino 原生对话框（AppKit NSSavePanel / GTK FileChooser）——
// 不能走 Win32 P/Invoke（comdlg32.dll / shell32.dll 不存在，一调就 DllNotFoundException）。
static class Dialogs
{
    // 跨平台「另存为」：Windows 用 Win32 GetSaveFileName（Photino 4.0.22 在 Windows 上
    // native Create() 会用 SHCreateItemFromParsingName 的返回值覆盖 CoCreateInstance 的成功 HRESULT，
    // 保存目标文件不存在解析必失败 0x80070002 → 对话框不弹直接返回 null，已实测复现；
    // Win32 版还能预填默认文件名）；macOS/Linux 用 Photino 原生对话框（AppKit/GTK 实现，不走 comdlg32）。
    // 返回用户选择的完整路径；取消返回 null。
    public static string? SaveFileDialog(PhotinoWindow window, string title, string defaultName, (string desc, string[] exts)[] filters)
    {
        if (OperatingSystem.IsWindows())
        {
            // 转成 OPENFILENAME 的 \0 分隔过滤器串：desc\0ext1;ext2\0...\0
            var winFilter = string.Concat(filters.Select(f => $"{f.desc}\0{string.Join(';', f.exts)}\0")) + "\0";
            return SaveFile(title, defaultName, winFilter, null);
        }
        if (OperatingSystem.IsLinux())
        {
            // Linux：Photino 原生 ShowSaveFile 把 defaultPath 当目录传给 gtk_file_chooser_set_current_folder，
            // 传文件名必然失败 → 对话框文件名框为空（用户报的「下载没有默认名字」）。
            // 用 GTK3 P/Invoke 自建保存对话框预填文件名；失败回退 Photino 原生对话框（仍可用，只是没预填名）。
            return GtkDialogs.SaveFile(window, title, defaultName, filters);
        }
        var picked = window.ShowSaveFile(title, defaultName, filters);
        return string.IsNullOrEmpty(picked) ? null : picked;
    }

    // 跨平台「选文件夹」：Windows 用 SHBrowseForFolder；macOS/Linux 用 Photino 原生对话框。
    public static string? PickFolderDialog(PhotinoWindow window, string title)
    {
        if (OperatingSystem.IsWindows())
            return PickFolder(title);
        var dirs = window.ShowOpenFolder(title, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), multiSelect: false);
        return dirs is { Length: > 0 } ? dirs[0] : null;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OPENFILENAME ofn);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    // 选一个文件夹；取消返回 null
    public static string? PickFolder(string title)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = IntPtr.Zero,
            pidlRoot = IntPtr.Zero,
            pszDisplayName = new string('\0', 260),
            lpszTitle = title,
            // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE（新式对话框，可输入路径）
            ulFlags = 0x0001 | 0x0040,
        };
        var pidl = SHBrowseForFolder(ref bi);
        if (pidl == IntPtr.Zero) return null;
        try
        {
            var buf = new StringBuilder(260);
            return SHGetPathFromIDList(pidl, buf) ? buf.ToString() : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    // 返回用户选择的完整路径；取消返回 null。
    public static string? SaveFile(string title, string defaultName, string filter, string? defExt)
    {
        var ofn = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            hwndOwner = IntPtr.Zero,
            lpstrFilter = filter,
            nFilterIndex = 1,
            // 用定长填充串而非 StringBuilder：实测 Marshal.SizeOf 对含 StringBuilder 的结构体
            // 报“无法计算大小”。marshaler 按串长分配缓冲区（此处 1024 字符 + 终止符），
            // 预填默认文件名（对话框文件名框直接显示），并允许对话框写回最长 1024 字符的路径。
            lpstrFile = (defaultName ?? "").PadRight(1024, '\0'),
            nMaxFile = 1024,
            lpstrTitle = title,
            lpstrDefExt = defExt,
            // OFN_OVERWRITEPROMPT | OFN_HIDEREADONLY | OFN_PATHMUSTEXIST
            Flags = 0x00000002 | 0x00000004 | 0x00000800,
        };
        if (!GetSaveFileName(ref ofn)) return null;
        // 回读结果按第一个 \0 截断（缓冲区剩余部分是填充的 \0）
        var file = ofn.lpstrFile;
        var nul = file.IndexOf('\0');
        return nul >= 0 ? file.Substring(0, nul) : file;
    }
}

// ---- Linux GTK3 原生「另存为」对话框 ----
// Photino.Native 的 Linux ShowSaveFile 把 defaultPath 传给 gtk_file_chooser_set_current_folder（目录），
// 传文件名必然失败 → 保存对话框文件名框为空（用户报的「下载文件没有默认名字」）。
// 这里 P/Invoke GTK3 自建保存对话框，调 gtk_file_chooser_set_current_name 预填文件名；
// 任何异常回退 Photino 原生对话框（功能仍可用，只是没有预填名）。
// 进程本身已加载 libgtk-3/libglib-2（Photino 用 WebKitGTK），且调用发生在 GTK 主线程，直接调即可。
static class GtkDialogs
{
    const int GTK_RESPONSE_ACCEPT = -3;
    const int GTK_RESPONSE_CANCEL = -6;
    const int GTK_FILE_CHOOSER_ACTION_SAVE = 1;

    public static string? SaveFile(PhotinoWindow window, string title, string defaultName, (string desc, string[] exts)[] filters)
    {
        try
        {
            var dialog = gtk_dialog_new();
            try
            {
                var chooser = gtk_file_chooser_widget_new(GTK_FILE_CHOOSER_ACTION_SAVE);
                gtk_box_pack_start(gtk_dialog_get_content_area(dialog), chooser, true, true, 0);
                gtk_window_set_title(dialog, title);
                // 自建对话框不像 GtkFileChooserDialog 那样自动带尺寸：给个合适的默认大小，保证文件列表可见
                gtk_window_set_default_size(dialog, 760, 480);
                gtk_dialog_add_button(dialog, "_取消", GTK_RESPONSE_CANCEL);
                gtk_dialog_add_button(dialog, "_保存", GTK_RESPONSE_ACCEPT);
                gtk_dialog_set_default_response(dialog, GTK_RESPONSE_ACCEPT);
                gtk_file_chooser_set_do_overwrite_confirmation(chooser, true);
                if (!string.IsNullOrEmpty(defaultName))
                    gtk_file_chooser_set_current_name(chooser, defaultName);
                foreach (var (desc, exts) in filters)
                {
                    var filter = gtk_file_filter_new();
                    gtk_file_filter_set_name(filter, desc);
                    foreach (var ext in exts) gtk_file_filter_add_pattern(filter, ext);
                    gtk_file_chooser_add_filter(chooser, filter);
                }
                // 关键：gtk_dialog_run 内部只 gtk_widget_show(dialog)，GtkDialog::show 只显示自己的
                // 内容区/按钮区、不会递归显示 pack 进去的 GtkFileChooserWidget —— 必须 show_all，
                // 否则弹出来只有标题和按钮、中间选路径的区域是空的（用户实测“连选路径的地方都没”）。
                gtk_widget_show_all(dialog);
                var res = gtk_dialog_run(dialog);
                if (res != GTK_RESPONSE_ACCEPT) return null;
                var ptr = gtk_file_chooser_get_filename(chooser);
                if (ptr == IntPtr.Zero) return null;
                var path = Marshal.PtrToStringUTF8(ptr);
                g_free(ptr);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally
            {
                gtk_widget_destroy(dialog);
            }
        }
        catch
        {
            // GTK 库缺失等异常：回退 Photino 原生对话框（无预填文件名）
            var picked = window.ShowSaveFile(title, defaultName, filters);
            return string.IsNullOrEmpty(picked) ? null : picked;
        }
    }

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_dialog_new();

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_dialog_get_content_area(IntPtr dialog);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_dialog_add_button(IntPtr dialog, [MarshalAs(UnmanagedType.LPUTF8Str)] string buttonText, int responseId);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_dialog_set_default_response(IntPtr dialog, int responseId);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int gtk_dialog_run(IntPtr dialog);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_file_chooser_widget_new(int action);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_chooser_set_current_name(IntPtr chooser, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_chooser_set_do_overwrite_confirmation(IntPtr chooser, bool confirm);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_chooser_add_filter(IntPtr chooser, IntPtr filter);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_file_chooser_get_filename(IntPtr chooser);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_file_filter_new();

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_filter_set_name(IntPtr filter, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_filter_add_pattern(IntPtr filter, [MarshalAs(UnmanagedType.LPUTF8Str)] string pattern);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_box_pack_start(IntPtr box, IntPtr child, bool expand, bool fill, uint padding);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_window_set_title(IntPtr window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_widget_show_all(IntPtr widget);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_widget_destroy(IntPtr widget);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_window_set_default_size(IntPtr window, int width, int height);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_free(IntPtr mem);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct BROWSEINFO
{
    public IntPtr hwndOwner;
    public IntPtr pidlRoot;
    public string pszDisplayName;
    public string lpszTitle;
    public int ulFlags;
    public IntPtr lpfn;
    public IntPtr lParam;
    public int iImage;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
struct OPENFILENAME
{
    public int lStructSize;
    public IntPtr hwndOwner;
    public IntPtr hInstance;
    public string? lpstrFilter;
    public string? lpstrCustomFilter;
    public int nMaxCustFilter;
    public int nFilterIndex;
    public string lpstrFile;
    public int nMaxFile;
    public string? lpstrFileTitle;
    public int nMaxFileTitle;
    public string? lpstrInitialDir;
    public string? lpstrTitle;
    public int Flags;
    public short nFileOffset;
    public short nFileExtension;
    public string? lpstrDefExt;
    public IntPtr lCustData;
    public IntPtr lpfnHook;
    public string? lpTemplateName;
    public IntPtr pvReserved;
    public int dwReserved;
    public int FlagsEx;
}

// 前端 window.external.sendMessage 发的请求结构（目前支持 op=saveFile/downloadFile/downloadMany/downloadManyCancel/uploadDropped/uploadDroppedCancel）
sealed class DesktopRequest
{
    public string? Op { get; set; }
    public string? Id { get; set; } // downloadMany/uploadDropped 任务 id：取消/结果回执按此对应
    public string? DefaultName { get; set; }
    public string? Content { get; set; }
    public string? Url { get; set; }
    public string[]? Paths { get; set; }
    public string? ConnId { get; set; } // 拖拽上传：连接 id
    public string? Dir { get; set; } // 拖拽上传：目标远端目录（前端 cwd）
    public string? Token { get; set; } // 拖拽上传：鉴权 token（/api/upload 需要）
    public string? BaseUrl { get; set; } // 拖拽上传：本地 Kestrel 地址（location.origin）
}
