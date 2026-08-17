using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

// 在 STA 线程上创建并运行 Photino 窗口（见上方坑 2；WaitForClose 阻塞该线程跑消息循环）
var uiThread = new Thread(() =>
{
    var window = new PhotinoWindow()
        // 标题带版本号：版本从主项目程序集读取（WebHost.AppVersion），与 HX 独立运行的版本一致
        .SetTitle("彗星ssh v" + WebHost.AppVersion())
        .SetUseOsDefaultSize(false)
        .SetSize(1280, 800)
        .Center()
        .SetDevToolsEnabled(true)
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
            var window = (PhotinoWindow)sender;
            DesktopRequest? req = null;
            try
            {
                req = JsonSerializer.Deserialize<DesktopRequest>(message, DesktopBridge.JsonOpts);
                if (req?.Op == "saveFile")
                {
                    // 用 Win32 GetSaveFileName 而非 Photino 的 ShowSaveFile：Photino 4.0.22 native Create()
                    // 会用 SHCreateItemFromParsingName 的返回值覆盖 CoCreateInstance 的成功 HRESULT，保存目标
                    // 文件不存在、解析必失败（0x80070002）→ 对话框不弹直接返回 null（已实测复现）。
                    // Win32 版还能预填默认文件名，不再需要用户手动输入。
                    var path = Win32Dialogs.SaveFile(
                        "导出连接",
                        req.DefaultName ?? "hxsfm-connections.json",
                        "JSON 文件 (*.json)\0*.json\0所有文件 (*.*)\0*.*\0",
                        "json");
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
                    var folder = Win32Dialogs.PickFolder("选择下载保存文件夹");
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
                    var path = Win32Dialogs.SaveFile(
                        "下载文件",
                        req.DefaultName ?? "download",
                        "所有文件 (*.*)\0*.*\0",
                        string.IsNullOrEmpty(srcExt) ? null : srcExt.TrimStart('.'));
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
            }
            catch (Exception ex)
            {
                // 按实际 op 回对应 Result，避免前端对应的一次性回调永远收不到而挂起
                var op = req?.Op switch
                {
                    "saveFile" => "saveFileResult",
                    "downloadFile" => "downloadFileResult",
                    "downloadMany" => "downloadManyResult",
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

// ---- Win32 原生「另存为」对话框 ----
// 用 GetSaveFileName 替代 Photino 的 ShowSaveFile：Photino 4.0.22 的 native Create() 会用
// SHCreateItemFromParsingName 的返回值覆盖 CoCreateInstance 的成功 HRESULT（保存目标文件不存在、
// 解析必失败 0x80070002）→ 对话框不弹直接返回 null（已实测复现）。Win32 版支持预填默认文件名。
static class Win32Dialogs
{
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

// 前端 window.external.sendMessage 发的请求结构（目前支持 op=saveFile/downloadFile/downloadMany/downloadManyCancel）
sealed class DesktopRequest
{
    public string? Op { get; set; }
    public string? Id { get; set; } // downloadMany 任务 id：取消/结果回执按此对应
    public string? DefaultName { get; set; }
    public string? Content { get; set; }
    public string? Url { get; set; }
    public string[]? Paths { get; set; }
}
