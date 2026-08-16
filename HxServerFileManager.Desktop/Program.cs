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
