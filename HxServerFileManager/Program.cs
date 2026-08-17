using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using HxSimpleWebAuth;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

// ----------------------------------------------------------------------------
// HxServerFileManager —— 基于 Kestrel 的 Web 服务
// 保持控制台(OutputType=Exe)，通过 Kestrel 提供前端界面，
// 后端使用 SSH.NET 连接并管理 Linux 服务器（SSH 命令 + SFTP 文件管理）。
//
// 新增能力：
//   1) 连接信息持久化到服务器（Data/connections.json）
//   2) 文本文件在线读取 / 编辑（/api/file-content）
//   3) 实时操作日志（/api/logs/stream，Server-Sent Events）
//
// 注意：connections.json 落盘时经 AES-GCM 加密（密钥见 DataCrypto，可配 HXSFM_DATA_KEY
// 环境变量或 Data/secret.key 文件），仅供本地/内网测试使用，请勿在公网环境直接暴露本服务。
// ----------------------------------------------------------------------------

// ----------------------------------------------------------------------------
// Web 服务入口：桌面壳（HxServerFileManager.Desktop）与独立运行共用此构建逻辑。
//   独立运行：dotnet run —— 走 top-level 的 WebHost.Build(args).Run()
//   桌面壳：  Photino 窗口 —— WebHost.Build(args) 后台 RunAsync()，前台弹 WebView
// ----------------------------------------------------------------------------

var app = WebHost.Build(args);

// Ctrl+C / SIGTERM 优雅停机：app.Run() 默认 ConsoleLifetime 已捕获信号并触发
// ApplicationStopping（进而取消后台任务），这里仅在停止流程开始时打一行提示。
app.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine("[HxServerFileManager] 收到停止信号，正在优雅停止…");
});

app.Run();
Console.WriteLine("[HxServerFileManager] 已退出");

/// <summary>
/// 构建并配置 WebApplication（中间件 + 路由 + 服务注册），但不启动。
/// 独立运行与桌面壳共用此方法。
/// </summary>
public static partial class WebHost
{
    public static WebApplication Build(string[] args)
    {
        // ---- ContentRoot 解析 ----
        // ASP.NET 默认把 ContentRoot 设为启动时 cwd，但 GUI 应用经 Finder / 文件管理器
        // 双击启动时 cwd 是 /（或不在程序目录），会找不到 wwwroot（白屏）并在错误位置写 Data。
        // 这里改为与位置无关的顺序：
        //   1) 环境变量 HXSFM_CONTENT_ROOT（显式指定，最优先）；
        //   2) 可执行文件所在目录（内含 wwwroot 时 —— 桌面壳 .app / 独立运行都满足）；
        //   3) 回退 cwd（保持"从启动目录读配置"的旧行为，迁移期兜底）。
        // ⚠️ 必须在 CreateBuilder 之前算好经 WebApplicationOptions 传入：新建 builder 后
        //    Environment.ContentRootPath / UseContentRoot 修改都会被 Build() 阶段重新解析覆盖
        //    （.NET 8+ 实测无效），options.ContentRootPath 是唯一权威入口。
        var contentRoot = Environment.GetEnvironmentVariable("HXSFM_CONTENT_ROOT");
        if (string.IsNullOrEmpty(contentRoot))
        {
            var exeDir = AppContext.BaseDirectory;
            contentRoot = Directory.Exists(Path.Combine(exeDir, "wwwroot"))
                ? exeDir
                : Directory.GetCurrentDirectory();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
        });

        // 显式配置 Kestrel（端口可由环境变量 PORT 覆盖，默认 15511）
        var listenPort = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 15511;
        // 单文件上传上限（MB）：0 或负数 = 不限制。
        // 优先级：环境变量 HXSFM_MAX_UPLOAD_MB（可覆盖）→ configs/env.json 的 maxUploadMb（模板见 env.json.example）
        //         → 默认 1024（1GB）。桌面壳（HxServerFileManager.Desktop）启动时强制设 0（忽略大小限制）。
        // 前端通过 /api/health 的 maxUploadBytes 读取做预校验（0 = 不限制）
        var maxUploadMb = 1024;
        var envUploadMb = Environment.GetEnvironmentVariable("HXSFM_MAX_UPLOAD_MB");
        if (!string.IsNullOrEmpty(envUploadMb) && int.TryParse(envUploadMb, out var envMb))
            maxUploadMb = envMb;
        else if (LoadConfigMaxUploadMb(builder.Environment.ContentRootPath) is int cfgMb)
            maxUploadMb = cfgMb;
        if (maxUploadMb < 0) maxUploadMb = 0;
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(listenPort);
            // 允许较大文件上传（默认 1GB；HXSFM_MAX_UPLOAD_MB 或 env.json 的 maxUploadMb 可配置；
            // 0 = 不限制，桌面壳即为该模式）
            kestrel.Limits.MaxRequestBodySize = maxUploadMb > 0 ? maxUploadMb * 1024 * 1024L : null;
            // 关掉响应最低速率限制（默认 240 B/s + 5s 宽限，对响应同样生效）：大文件/慢速远端
            // 下载时响应稍慢会被 Kestrel 掐断（表现为“下载超时”），本地应用没必要限速
            kestrel.Limits.MinResponseDataRate = null;
        });

        // 单例：会话表 / 操作日志 / 连接存储
        builder.Services.AddSingleton<ConnectionManager>();
        builder.Services.AddSingleton<OperationLogger>();
        var dataDir = Path.Combine(builder.Environment.ContentRootPath, "Data");
        builder.Services.AddSingleton(new ConnectionsStore(dataDir));
        // 用户偏好设置：常用目录收藏 + 终端宏（Data/settings.json）
        builder.Services.AddSingleton(new SettingsStore(dataDir));

        var app = builder.Build();

// ----------------------------------------------------------------------------
// 全局停机令牌：收到停止信号（Ctrl+C / SIGTERM / 桌面壳关窗）时统一收尾。
//
// 背景：/api/logs/stream、/api/terminal/stream、/api/terminal/ws 都是"永不自行结束"的长连接
// （SSE 一直推、WebSocket 一直等输入）。Kestrel 优雅停机时对这类进行中的请求只能干等
// HostOptions.ShutdownTimeout（默认 30s）超时强杀 —— 表现就是"收到停止信号但进程半天不退"，
// docker stop 默认 10s 后还会直接 SIGKILL。因此必须在停机开始时主动取消这些循环。
//
// shutdownCts 必须在路由注册之前创建（后面的路由 lambda 要引用它）。停机时：
//   1) shutdownCts.Cancel() —— 三个长连接端点都把该令牌链进各自的取消源，循环立即结束；
//   2) 后台关闭全部 SSH 会话（DisposeShell 让终端输出 channel 完成，读它的循环一并收尾；
//      SSH/SFTP 断开则清掉 keepalive 等后台占用）。放后台执行，避免 SSH.NET 断开阻塞停机。
// ----------------------------------------------------------------------------
var shutdownCts = new CancellationTokenSource();
var mgr = app.Services.GetRequiredService<ConnectionManager>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    shutdownCts.Cancel();
    _ = Task.Run(() => mgr.DisposeAll());
});
_ = Task.Run(() => mgr.CleanupLoop(shutdownCts.Token));

// 启用 WebSocket 支持（交互终端 /api/terminal/ws 依赖）
app.UseWebSockets();

// ----------------------------------------------------------------------------
// 登录鉴权（HxSimpleWebAuth）：密码来源优先级： 
//   1) 环境变量 HXSFM_WEB_PASSWORD（可覆盖，方便 CI/Docker 注入）；
//   2) configs/env.json 的 authPwd 字段（模板见 configs/env.json.example，存密码用）。
// - 配置了密码：所有 /api（除 /api/session 与 /api/auth/*）必须带有效 Bearer token；
// - 未配置密码：仅允许本机回环来源访问（fail-closed，避免内网裸奔）；
// - SSE/下载等无法携带请求头的场景，前端把 token 放在 ?token= 查询参数，
//   中间件在此处统一转成 Authorization 头再交给库校验。
// ----------------------------------------------------------------------------
var adminPassword = Environment.GetEnvironmentVariable("HXSFM_WEB_PASSWORD");
if (string.IsNullOrEmpty(adminPassword))
    adminPassword = LoadConfigPassword(builder.Environment.ContentRootPath);
adminPassword ??= "";
var authRequired = !string.IsNullOrEmpty(adminPassword);
var auth = new WebAdminAuth(adminPassword, logDirectory: builder.Environment.ContentRootPath);

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api")
        || path.StartsWithSegments("/api/session")
        || auth.IsAuthPath(path.ToString()))
    {
        await next();
        return;
    }

    if (!authRequired)
    {
        if (IsLoopbackAddress(context.Connection.RemoteIpAddress))
        {
            await next();
            return;
        }

        await WriteAuthResponseAsync(context, ApiResponse.Error(403, "未配置访问密码（HXSFM_WEB_PASSWORD 或 configs/env.json），仅允许本机访问。"));
        return;
    }

    if (auth.Authorize(await CreateAuthRequestAsync(context)))
    {
        await next();
        return;
    }

    await WriteAuthResponseAsync(context, ApiResponse.Error(401, "Unauthorized."));
});

// 会话探测：前端据此决定显示登录页还是主界面
app.MapGet("/api/session", (HttpContext context) => Results.Ok(new
{
    required = authRequired,
    authenticated = !authRequired || auth.Authorize(CreateAuthRequest(context)),
}));

// 登录 / 登出（凭据校验、token 签发/吊销、失败锁定都由 HxSimpleWebAuth 处理）
app.MapPost("/api/auth/login", async (HttpContext context) =>
{
    var response = auth.Handle(await CreateAuthRequestAsync(context), context.Request.Path.ToString());
    await WriteAuthResponseAsync(context, response);
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    var response = auth.Handle(await CreateAuthRequestAsync(context), context.Request.Path.ToString());
    await WriteAuthResponseAsync(context, response);
});

if (authRequired)
    Console.WriteLine("[HxServerFileManager] 已启用登录鉴权（密码来源：HXSFM_WEB_PASSWORD 环境变量 或 configs/env.json）");
else
    Console.WriteLine("[HxServerFileManager] 未配置访问密码（HXSFM_WEB_PASSWORD / configs/env.json）：仅本机回环可访问");

// ---- 静态前端（wwwroot）----
var wwwroot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
if (!Directory.Exists(wwwroot)) Directory.CreateDirectory(wwwroot);
var wwwProvider = new PhysicalFileProvider(wwwroot);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = wwwProvider, RequestPath = "" });
app.UseStaticFiles(new StaticFileOptions { FileProvider = wwwProvider, RequestPath = "" });

// SPA 回退：未匹配的路径（非 /api）交给 index.html，方便以后加前端路由
app.MapFallbackToFile("index.html");

// ----------------------------------------------------------------------------
// REST API
// ----------------------------------------------------------------------------

// 连接一台 Linux 服务器（密码 或 私钥 认证），成功后把连接信息保存到服务器
app.MapPost("/api/connect", (ConnectRequest req, ConnectionManager mgr, ConnectionsStore store, OperationLogger log) =>
{
    if (string.IsNullOrWhiteSpace(req.Host) || string.IsNullOrWhiteSpace(req.Username))
        return Results.BadRequest(new { error = "Host 与 Username 为必填" });

    var port = req.Port is int pp && pp > 0 ? pp : 22;
    var (ok, id, home, err) = ConnectInternal(req, mgr);
    if (!ok)
    {
        log.Log("error", $"{req.Username}@{req.Host}:{port}", "连接", "认证/握手失败", err);
        return Results.BadRequest(new { error = err });
    }

    // 连接成功后持久化（相同 host:port:username 会更新而非新增）；返回最终 profile 供前端本地化引用
    var prof = store.Upsert(ToProfile(req, port));
    log.Log("info", $"{req.Username}@{req.Host}:{port}", "连接", "建立 SSH/SFTP 会话", "成功");
    return Results.Ok(new
    {
        connectionId = id,
        profileId = prof.Id,
        host = req.Host,
        username = req.Username,
        name = req.Name,
        homeDirectory = home,
        message = "连接成功"
    });
});

// 断开
app.MapPost("/api/disconnect", (IdRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    var s = mgr.GetSilently(req.ConnectionId);
    var label = s != null ? req.ConnectionId : req.ConnectionId;
    mgr.Remove(req.ConnectionId);
    log.Log("info", req.ConnectionId, "断开", "关闭 SSH/SFTP 会话");
    return Results.Ok(new { message = "已断开" });
});

// 列出已保存的连接（密码/私钥不返回，仅标记是否存在）
app.MapGet("/api/connections", (ConnectionsStore store) =>
{
    var list = store.List().Select(p => new
    {
        id = p.Id,
        name = p.Name,
        host = p.Host,
        port = p.Port,
        username = p.Username,
        authType = p.AuthType,
        hasPassword = !string.IsNullOrEmpty(p.Password),
        hasKey = !string.IsNullOrEmpty(p.PrivateKey),
        createdAt = p.CreatedAt,
        lastConnectedAt = p.LastConnectedAt
    }).OrderByDescending(p => p.lastConnectedAt).ToList();
    return Results.Ok(new { connections = list });
});

// 导出全部已保存连接（含密码/私钥/口令等凭据，明文 JSON）—— 用户主动下载，用于备份/迁移。
// 受同一认证中间件保护；文件内容即前端「导入」所需的数组格式。
app.MapGet("/api/connections/export", (ConnectionsStore store) =>
{
    var list = store.List();
    return Results.Ok(new { exportedAt = DateTime.Now, connections = list });
});

// 导入连接：接收明文 JSON 数组（host/username/port 必填，缺 id 自动补），
// 按 host|port|username 去重：已存在则更新凭据、保留原 Id/创建时间，否则新增。返回各类数量。
// 导入连接：接收明文 JSON 数组（host/username/port 必填，缺 id 自动补）。
// ?mode=merge（默认）：按 host|port|username|password 四字段全一致判重，重复则更新、否则新增；
// ?mode=replace：清空现有连接，整体替换为导入内容。返回各类数量。
app.MapPost("/api/connections/import", (List<ConnectionProfile> profiles, string? mode, ConnectionsStore store, OperationLogger log) =>
{
    var replace = string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase);
    var valid = new List<ConnectionProfile>();
    var skipped = 0;
    foreach (var p in profiles ?? new List<ConnectionProfile>())
    {
        if (string.IsNullOrWhiteSpace(p.Host) || string.IsNullOrWhiteSpace(p.Username) || p.Port <= 0)
        {
            skipped++;
            continue;
        }
        valid.Add(p with
        {
            Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
            Name = string.IsNullOrWhiteSpace(p.Name) ? $"{p.Username}@{p.Host}" : p.Name,
            AuthType = p.AuthType == "key" ? "key" : "password",
            CreatedAt = p.CreatedAt == default ? DateTime.Now : p.CreatedAt,
        });
    }

    if (replace)
    {
        store.ReplaceAll(valid);
        log.Log("info", "导入连接", "覆盖", $"导入 {valid.Count}，跳过 {skipped}", null);
        return Results.Ok(new { replaced = valid.Count, skipped });
    }

    var (added, updated) = store.MergeImport(valid);
    log.Log("info", "导入连接", "去重合并", $"新增 {added}，更新 {updated}，跳过 {skipped}", null);
    return Results.Ok(new { added, updated, skipped });
});

// 用已保存的凭据重新连接
app.MapPost("/api/connections/reconnect", (IdRequest req, ConnectionManager mgr, ConnectionsStore store, OperationLogger log) =>
{
    var prof = store.Get(req.ConnectionId);
    if (prof is null) return Results.NotFound(new { error = "未找到保存的连接" });

    var creq = new ConnectRequest(prof.Host, prof.Port, prof.Username, prof.Password, prof.PrivateKey, prof.Passphrase);
    var (ok, id, home, err) = ConnectInternal(creq, mgr);
    if (!ok)
    {
        log.Log("error", $"{prof.Username}@{prof.Host}:{prof.Port}", "重连", "失败", err);
        return Results.BadRequest(new { error = err });
    }
    store.Upsert(prof with { LastConnectedAt = DateTime.Now });
    log.Log("info", $"{prof.Username}@{prof.Host}:{prof.Port}", "重连", "成功");
    return Results.Ok(new
    {
        connectionId = id,
        profileId = prof.Id,
        host = prof.Host,
        username = prof.Username,
        name = prof.Name,
        homeDirectory = home,
        message = "连接成功"
    });
});

// 活跃 SSH 会话健康检查：前端轮询以此检测连接是否断开（SSH.NET 底层检测到
// 对端关闭/网络失败时 IsConnected 会自动变 false）。
app.MapGet("/api/connections/health", (ConnectionManager mgr) =>
{
    var list = mgr.ListHealth();
    return Results.Ok(new { sessions = list });
});

// 编辑已保存的连接（留空的字段保持不变；别名可任意设置）
app.MapPut("/api/connections/{id}", (string id, ConnectRequest req, ConnectionsStore store) =>
{
    var prof = store.Get(id);
    if (prof is null) return Results.NotFound(new { error = "未找到保存的连接" });

    var updated = prof with
    {
        Name = string.IsNullOrWhiteSpace(req.Name) ? prof.Name : req.Name.Trim(),
        Host = string.IsNullOrWhiteSpace(req.Host) ? prof.Host : req.Host.Trim(),
        Port = req.Port is int pp && pp > 0 ? pp : prof.Port,
        Username = string.IsNullOrWhiteSpace(req.Username) ? prof.Username : req.Username.Trim(),
        Password = string.IsNullOrEmpty(req.Password) ? prof.Password : req.Password,
        PrivateKey = string.IsNullOrEmpty(req.PrivateKey) ? prof.PrivateKey : req.PrivateKey,
        Passphrase = string.IsNullOrEmpty(req.Passphrase) ? prof.Passphrase : req.Passphrase,
        AuthType = string.IsNullOrWhiteSpace(req.PrivateKey)
            ? (string.IsNullOrWhiteSpace(req.Password) ? prof.AuthType : "password")
            : "key",
    };
    store.Upsert(updated);
    return Results.Ok(new { message = "已更新", id = updated.Id, name = updated.Name });
});

// 删除已保存的连接
app.MapDelete("/api/connections/{id}", (string id, ConnectionsStore store) =>
{
    store.Remove(id);
    return Results.Ok(new { message = "已删除" });
});

// 列出目录
app.MapGet("/api/files", (string connId, string? path, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(connId);
        var target = string.IsNullOrWhiteSpace(path) ? "." : path;
        var items = s.Sftp.ListDirectory(target)
            .Where(f => f.Name != "." && f.Name != "..")
            .Select(f => new FileEntry(
                f.Name,
                f.FullName,
                f.IsDirectory,
                f.Length,
                f.LastWriteTime.ToUniversalTime(),
                FileHelpers.LooksText(f.Name, f.Length)))
            .OrderByDescending(f => f.IsDirectory)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        log.Log("info", connId, "列目录", target);
        return Results.Ok(new { path = target, items });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 新建目录
app.MapPost("/api/mkdir", (PathRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        var full = CombinePath(req.Path, req.Name);
        s.Sftp.CreateDirectory(full);
        log.Log("info", req.ConnectionId, "新建目录", full);
        return Results.Ok(new { path = full });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 批量创建远端目录（上传文件夹用：父目录与空目录一并建，已存在则跳过，可重复执行）
app.MapPost("/api/ensure-dirs", (EnsureDirsRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        var created = 0;
        foreach (var name in req.Dirs ?? Array.Empty<string>())
        {
            EnsureRemoteDir(s, CombinePath(req.Path, name));
            created++;
        }
        log.Log("info", req.ConnectionId, "创建目录", $"{created} 个（上传文件夹）");
        return Results.Ok(new { created });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 重命名 / 移动
app.MapPost("/api/rename", (RenameRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        var full = CombinePath(req.Path, req.Name);
        s.Sftp.RenameFile(full, req.NewPath);
        log.Log("info", req.ConnectionId, "重命名", $"{full} -> {req.NewPath}");
        return Results.Ok(new { newPath = req.NewPath });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 删除（文件或目录）
app.MapPost("/api/delete", (PathRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        var full = CombinePath(req.Path, req.Name);
        if (s.Sftp.Exists(full) && s.Sftp.GetAttributes(full).IsDirectory)
            s.Sftp.DeleteDirectory(full);
        else
            s.Sftp.DeleteFile(full);
        log.Log("info", req.ConnectionId, "删除", full);
        return Results.Ok(new { deleted = full });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 上传文件（multipart）
app.MapPost("/api/upload", async (HttpContext ctx, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var form = await ctx.Request.ReadFormAsync();
        var connId = form["connId"].ToString();
        var dir = form["path"].ToString();
        var s = mgr.Get(connId);
        var uploaded = new List<string>();
        foreach (var file in form.Files)
        {
            if (file.Length == 0) continue;
            var remote = CombinePath(dir, file.FileName);
            using var src = file.OpenReadStream();
            s.Sftp.UploadFile(src, remote, true);
            uploaded.Add(remote);
        }
        log.Log("info", connId, "上传", $"{uploaded.Count} 个文件到 {dir}");
        return Results.Ok(new { uploaded });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 下载文件（流式）
// 手动流式下发（不用 Results.File）：① 大文件/慢速远端场景每写一块 Touch 一次，防止 30 分钟
// 空闲回收把正在下载的会话断掉；② 中途出错能捕获并返回明确错误（Results.File 的流由 Kestrel 托管，
// 出错只能默默中断连接，桌面端表现为“下载失败/超时”）。
app.MapGet("/api/download", async (string connId, string path, HttpContext ctx, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(connId);
        log.Log("info", connId, "下载", path);
        var name = Path.GetFileName(path);
        await using var stream = s.Sftp.OpenRead(path);
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(name)}";
        ctx.Response.ContentLength = stream.Length;
        var buf = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buf, ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
            s.Touch(); // 下载期间保持会话活跃，防空闲回收
        }
        // 响应体已直接写入（Response Started），此时返回 Results.Ok() 会再次 set_StatusCode 抛
        // “StatusCode cannot be set because the response has already started”，Kestrel 把已发送一半的
        // 连接直接掐断（客户端表现“Error while copying content to a stream / response ended prematurely”）。
        // 用 Results.Empty（不触碰状态码）让响应正常收尾。
        return Results.Empty;
    }
    catch (Exception ex)
    {
        // 响应头未发送时返回明确错误；已开始发送则只能中断（Kestrel 收尾，客户端收到连接重置）
        return ctx.Response.HasStarted ? Results.Empty : Results.BadRequest(new { error = ex.Message });
    }
});

// 批量下载（多选文件/文件夹 → 在远端 tar 打流直出，本地解包保留目录结构）。
// 桌面壳弹文件夹选择器选一个本地目录，把流解包到该目录；浏览器端无选文件夹能力，只能逐个下载文件。
// 打包在远端执行（tar 在 Linux 普遍预装），一个 exec 通道流式输出，不占本机内存。
// 注意：SshCommand.Execute() 会把全部输出缓冲进内存，这里必须 BeginExecute + 读 OutputStream 流式消费。
app.MapPost("/api/download-many", async (DownloadManyRequest req, HttpContext ctx, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var connId = req.ConnectionId ?? ctx.Request.Query["connId"].ToString();
        if (string.IsNullOrWhiteSpace(connId) || req.Paths is not { Length: > 0 } paths)
            return Results.BadRequest(new { error = "缺少连接或选中项" });
        var s = mgr.Get(connId);

        // 所有选中项必须来自同一目录（文件列表是单目录多选），共同父目录 = 第一项的父目录
        var parent = WebHost.ParentDir(paths[0]);
        foreach (var p in paths)
        {
            if (!Path.IsPathRooted(p))
                return Results.BadRequest(new { error = "路径必须为绝对路径：" + p });
            if (WebHost.ParentDir(p) != parent)
                return Results.BadRequest(new { error = "选中项不在同一目录，无法批量下载" });
            // 预校验存在性：tar 遇到不存在的项会整体失败，先拦下来给明确错误
            if (!s.Sftp.Exists(p))
                return Results.BadRequest(new { error = "路径不存在：" + p });
        }

        // 相对名逐个 Shq 转义（防空格/特殊字符），-C parent 切换基准目录。
        // 不用 --ignore-failed-read：busybox tar（Docker 常见）不支持该选项会直接失败；
        // 存在性已在上面预校验，剩下「存在但读不了」的极端情况让客户端解包报错即可。
        // -- 结束选项解析：选中项以 - 开头（如 -foo.txt）时 tar 会把它当选项导致整包 0 输出（实测
        // busybox tar 1.37 与 GNU tar 都支持 --）。
        var names = string.Join(' ', paths.Select(p => WebHost.Shq(p[parent.Length..].TrimStart('/'))));
        var cmdLine = $"tar -C {WebHost.Shq(parent)} -cf - -- {names}";

        log.Log("info", connId, "批量下载", $"{paths.Length} 项：{string.Join(", ", paths)}");
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.ContentDisposition =
            $"attachment; filename*=UTF-8''hxsfm-download-{DateTime.Now:yyyyMMdd-HHmmss}.tar";

        using var cmd = s.Ssh.CreateCommand(cmdLine);
        cmd.CommandTimeout = TimeSpan.FromHours(2);
        var ar = cmd.BeginExecute(); // 不阻塞：OutputStream 边产边读（Execute() 会全部缓冲进内存）
        try
        {
            await using var outStream = cmd.OutputStream;
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await outStream.ReadAsync(buf, ctx.RequestAborted)) > 0)
            {
                await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                s.Touch(); // 长时间传输期间保持会话活跃，防空闲回收
            }
            cmd.EndExecute(ar); // 流读完再收尾；中途异常则跳过（using 关闭通道会终止远端 tar）
        }
        catch (OperationCanceledException) { throw; } // 客户端断开：Dispose 关通道终止远端 tar
        // 同 /api/download：响应体已开始，返回 Results.Ok() 会抛“StatusCode cannot be set because
        // the response has already started”导致 Kestrel 掐断连接（客户端报 response ended prematurely），
        // 必须用 Results.Empty 正常收尾。
        return Results.Empty;
    }
    catch (Exception ex)
    {
        // 响应头未发送时返回明确错误；已开始发送则只能中断
        return ctx.Response.HasStarted ? Results.Empty : Results.BadRequest(new { error = ex.Message });
    }
});

// 读取文本文件内容（用于在线编辑；二进制/超大文件会被拒绝）
// 直接以原始字节流返回（不再 JSON 包裹）：System.Text.Json 默认把所有非 ASCII（中文等）转义成
// \uXXXX，大文本会膨胀 3-6 倍，且服务端转义 + 浏览器 JSON.parse 都极耗 CPU —— 这是双击打开
// 明显慢于终端 cat 的主因。改为流式下发：前端边收边显示（像 cat 一样渐进出现），首字节只等
// 一个 SFTP 读块。
app.MapGet("/api/file-content", async (string connId, string path, HttpContext ctx, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(connId);
        // OpenRead 内部已经会取得文件属性；不要先 GetAttributes 再 OpenRead，
        // 否则高延迟服务器会在首字节前白白多付一次 SFTP 往返。
        using var stream = s.Sftp.OpenRead(path);
        var fileLength = stream.Length;
        if (fileLength > FileHelpers.MaxEditBytes) return Results.BadRequest(new { error = "文件过大，暂不支持在线编辑（>10MB）" });

        // 先读开头 8KB 做二进制嗅探：真实二进制（可执行/图片/压缩包）头部通常有 NUL 字节，
        // 前缀嗅探即可拦下绝大多数；通过后才开始流式下发（NUL 只出现在 8KB 之后的极端
        // 文本文件会以替换字符显示，可接受）。
        var sniffLen = (int)Math.Min(8 * 1024, fileLength);
        var sniff = new byte[sniffLen];
        var sniffRead = sniffLen > 0 ? await stream.ReadAsync(sniff, ctx.RequestAborted) : 0;
        if (sniff.AsSpan(0, sniffRead).IndexOf((byte)0) >= 0)
            return Results.BadRequest(new { error = "该文件疑似二进制，无法在浏览器中编辑" });

        log.Log("info", connId, "读取文件", path);
        // 直接写响应体流式返回原始字节（与 SSE 处理器同一模式；Content-Length 让前端能算进度）。
        // 注意：一旦开始写响应体就不能再返回 JSON 错误，中途异常只能让连接中断。
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = fileLength;
        if (sniffRead > 0) await ctx.Response.Body.WriteAsync(sniff.AsMemory(0, sniffRead), ctx.RequestAborted);
        var buf = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buf, ctx.RequestAborted)) > 0)
            await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
        return Results.Empty;
    }
    catch (Exception ex)
    {
        // 响应未开始时才能正常返回 JSON 错误（GetAttributes/OpenRead/嗅探阶段）；
        // 流中途失败时响应已开始，返回结果只会再抛错，直接让连接中断即可
        if (!ctx.Response.HasStarted)
            return Results.BadRequest(new { error = ex.Message });
        throw;
    }
});

// 保存文本文件内容（覆盖写回远端）
app.MapPut("/api/file-content", (FileContentRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        var bytes = Encoding.UTF8.GetBytes(req.Content ?? "");
        using var ms = new MemoryStream(bytes);
        s.Sftp.UploadFile(ms, req.Path, true);
        log.Log("info", req.ConnectionId, "保存文件", $"{req.Path} ({bytes.Length} 字节)");
        return Results.Ok(new { saved = req.Path, size = bytes.Length });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 执行命令（阻塞至命令结束）。
// SSH.NET 每次 CreateCommand 都会开新的 exec 通道，cd 等目录状态默认不保留；
// 这里在会话里记录 cwd，命令包装为 cd <cwd> && <cmd>; rc=$?; pwd; exit $rc，
// 这样目录会跨命令保留，并返回最新 cwd 供前端（终端提示符 / 文件列表联动）使用。
app.MapPost("/api/command", (CommandRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        var cwd = string.IsNullOrWhiteSpace(s.Cwd) ? "/" : s.Cwd;
        var wrapped = $"cd {Shq(cwd)} && {req.Command ?? ""}; rc=$?; pwd; exit $rc";
        using var cmd = s.Ssh.CreateCommand(wrapped);
        cmd.Execute();

        var output = (cmd.Result ?? "").TrimEnd('\r', '\n');
        var newCwd = cwd;
        var lines = output.Split('\n');
        var last = lines[^1].Trim();
        if (last.StartsWith('/'))
        {
            newCwd = last;
            output = string.Join('\n', lines.Take(lines.Length - 1));
        }
        s.Cwd = newCwd;

        log.Log("info", req.ConnectionId, "执行命令", req.Command ?? "", $"exit={cmd.ExitStatus} cwd={newCwd}");
        return Results.Ok(new
        {
            output,
            error = cmd.Error ?? "",
            exitStatus = cmd.ExitStatus,
            cwd = newCwd
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// ----------------------------------------------------------------------------
// 服务器间直传（不经本机中转）：在源服务器上执行 scp 把选中项复制到目标服务器。
// 数据路径 A -> B 全程在两端服务器之间流动；本机只下发指令并轮询每项状态。
// ----------------------------------------------------------------------------

// 发起直传：返回 jobId，前端轮询 /api/server-copy/{jobId} 查看进度。
// 目标目录不存在时先用目标机已有 SFTP 会话补建（免去在源机上拼 mkdir 命令）。
app.MapPost("/api/server-copy", (ServerCopyRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var src = mgr.Get(req.SourceConnId);
        var dst = mgr.Get(req.TargetConnId);
        if (req.Items is not { Length: > 0 })
            return Results.BadRequest(new { error = "请先选中要发送的文件或文件夹" });
        foreach (var p in req.Items)
            if (string.IsNullOrWhiteSpace(p) || !p.StartsWith('/'))
                return Results.BadRequest(new { error = $"源路径必须是绝对路径：{p}" });
        if (string.IsNullOrWhiteSpace(req.TargetDir) || !req.TargetDir.TrimStart().StartsWith('/'))
            return Results.BadRequest(new { error = "目标目录必须是绝对路径（以 / 开头）" });

        // 同一台服务器（host|port|username 相同）直接拒绝：服务器间直传才有意义
        if (string.Equals(src.Host, dst.Host, StringComparison.OrdinalIgnoreCase)
            && src.Port == dst.Port
            && string.Equals(src.Username, dst.Username, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "目标连接与源是同一台服务器，请直接复制/重命名" });

        var targetDir = req.TargetDir.TrimEnd('/');
        if (targetDir.Length == 0) targetDir = "/";
        try { EnsureRemoteDir(dst, targetDir); }
        catch (Exception ex) { return Results.BadRequest(new { error = $"创建目标目录失败：{ex.Message}" }); }

        var srcLabel = $"{src.Username}@{src.Host}:{src.Port}";
        var dstLabel = $"{dst.Username}@{dst.Host}:{dst.Port}";
        var job = ServerCopyJobs.Add(new ServerCopyJob(srcLabel, dstLabel, req.Items, targetDir));
        ServerCopyJobs.Start(job, src, dst, log);
        log.Log("info", srcLabel, "服务器直传", $"{req.Items.Length} 项 -> {dstLabel}:{targetDir}", "已启动");
        return Results.Ok(new { jobId = job.Id, total = job.Total });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 直传进度：每项状态（pending/running/done/failed）+ 总体 state
app.MapGet("/api/server-copy/{jobId}", (string jobId) =>
{
    var job = ServerCopyJobs.Get(jobId);
    if (job is null) return Results.NotFound(new { error = "任务不存在或已过期" });
    return Results.Ok(new
    {
        id = job.Id,
        total = job.Total,
        done = job.Done,
        state = job.State,
        error = job.Error,
        source = job.SourceLabel,
        target = job.TargetLabel,
        targetDir = job.TargetDir,
        items = job.ItemStates
    });
});

// ----------------------------------------------------------------------------
// 交互终端（带 pty 的 SSH shell）：可运行 nano/vim/top 及需要读输入的脚本。
// 输出经 SSE 流式推送，输入通过 POST 写回 stdin。
// ----------------------------------------------------------------------------

// 打开交互终端（每会话一个 shell，惰性创建；pty 尺寸创建后不可变）
app.MapPost("/api/terminal/open", (TerminalOpenRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        var cols = req.Cols is int c and > 0 and < 500 ? (uint)c : 100;
        var rows = req.Rows is int r and > 0 and < 200 ? (uint)r : 30;
        s.EnsureShell(cols, rows);
        log.Log("info", req.ConnectionId, "打开交互终端", $"{cols}x{rows}");
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 交互终端输出流（SSE：先回放最近输出，再实时推送）
app.MapGet("/api/terminal/stream", async (string connId, HttpContext ctx, ConnectionManager mgr) =>
{
    var s = mgr.Get(connId);
    ShellStream? shell;
    Channel<byte[]>? ch;
    lock (s.ShellLock) { shell = s.Shell; ch = s.ShellOutput; }
    if (shell is null || ch is null)
        return Results.BadRequest(new { error = "终端未打开，请先 POST /api/terminal/open" });

    ctx.Response.Headers.Append("Content-Type", "text/event-stream");
    ctx.Response.Headers.Append("Cache-Control", "no-cache");
    ctx.Response.Headers.Append("Connection", "keep-alive");
    ctx.Response.Headers.Append("X-Accel-Buffering", "no");

    // 客户端断开或服务停机（shutdownCts）都会取消本循环，否则停机会被这个长连接拖住
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, shutdownCts.Token);

    string tail;
    lock (s.ShellTail) tail = s.ShellTail.ToString();
    if (tail.Length > 0)
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type = "out", data = tail })}\n\n", cts.Token);
    await ctx.Response.Body.FlushAsync();

    try
    {
        await foreach (var chunk in ch.Reader.ReadAllAsync(cts.Token))
        {
            var data = Encoding.UTF8.GetString(chunk);
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type = "out", data })}\n\n", cts.Token);
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { /* 客户端断开 / 服务停机 */ }
    // 响应已开始，不能返回 Results.Ok()（会因重复设置状态码抛异常）；Empty 不触碰状态码
    return Results.Empty;
});

// ----------------------------------------------------------------------------
// 交互终端 WebSocket：双向通道，输入输出共一条连接，替代「SSE 输出 + POST 输入」。
// 鉴权由上方中间件统一处理（?token= 查询参数 → Authorization 头）。
// 消息格式（JSON 文本帧）：
//   入站 { "type": "input", "data": "..." }   写入 shell stdin
//   入站 { "type": "resize", "cols": 80, "rows": 24 }
//   出站 { "type": "out", "data": "..." }     shell stdout（含 OSC 7）
//   出站 { "type": "closed", "reason": "..." } shell 已关闭
// ----------------------------------------------------------------------------
app.MapGet("/api/terminal/ws", async (string connId, HttpContext ctx, ConnectionManager mgr) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
        return Results.BadRequest(new { error = "需要 WebSocket 请求" });

    SshSession s;
    try { s = mgr.Get(connId); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    ShellStream? shell;
    Channel<byte[]>? ch;
    lock (s.ShellLock) { shell = s.Shell; ch = s.ShellOutput; }
    if (shell is null || ch is null || !s.ShellAlive)
    {
        await SendWsJsonAsync(ws, new { type = "closed", reason = "终端未打开，请先 POST /api/terminal/open" }, ctx.RequestAborted);
        return Results.Empty;
    }

    // shell 侧结束（SSH 断开/对端关闭）或服务停机（shutdownCts）时用它取消入站读取循环，
    // 否则主循环会一直阻塞在 ReceiveAsync 上，ws 不关，停机也会被这个请求拖住。
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, shutdownCts.Token);

    // 输出转发任务：shell 输出 channel → WebSocket
    var pump = Task.Run(async () =>
    {
        try
        {
            // 先回放 tail，再实时推送（与 SSE 行为一致）
            string tail;
            lock (s.ShellTail) tail = s.ShellTail.ToString();
            if (tail.Length > 0)
                await SendWsJsonAsync(ws, new { type = "out", data = tail }, linked.Token);

            await foreach (var chunk in ch.Reader.ReadAllAsync(linked.Token))
            {
                var data = Encoding.UTF8.GetString(chunk);
                await SendWsJsonAsync(ws, new { type = "out", data }, linked.Token);
            }
            // channel 正常完成 = shell 已被回收（SSH 断开或主动关闭）：告知前端
            try
            {
                await SendWsJsonAsync(ws, new { type = "closed", reason = "SSH 连接已断开" }, CancellationToken.None);
            }
            catch { /* ws 可能已不可写 */ }
        }
        catch (OperationCanceledException) { /* 客户端断开 */ }
        catch (Exception) { /* channel 关闭等 */ }
        finally
        {
            // 唤醒主循环，让它结束并关闭 ws（前端 onclose 触发断开提示）
            try { linked.Cancel(); } catch { }
        }
    }, ctx.RequestAborted);

    // 主循环：读取入站 WebSocket 帧 → shell stdin
    try
    {
        var buf = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(buf, linked.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            var text = Encoding.UTF8.GetString(buf, 0, result.Count);

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("type", out var typeEl))
                {
                    var t = typeEl.GetString();
                    lock (s.ShellLock)
                    {
                        if (s.Shell is null || !s.ShellAlive)
                            break;

                        if (t == "input" && doc.RootElement.TryGetProperty("data", out var dataEl))
                        {
                            s.Shell.Write(dataEl.GetString() ?? "");
                            s.Shell.Flush();
                        }
                        else if (t == "resize"
                                 && doc.RootElement.TryGetProperty("cols", out var colsEl)
                                 && doc.RootElement.TryGetProperty("rows", out var rowsEl)
                                 && colsEl.TryGetInt32(out var cols) && rowsEl.TryGetInt32(out var rows))
                        {
                            // 前端容器尺寸变化（拉窗/拖分隔条/最大化）时同步 pty 尺寸，
                            // 让 shell 的回绕列数跟随终端实际宽度
                            cols = Math.Clamp(cols, 40, 200);
                            rows = Math.Clamp(rows, 10, 60);
                            s.Shell.ChangeWindowSize((uint)cols, (uint)rows, (uint)(cols * 8), (uint)(rows * 16));
                        }
                    }
                }
            }
            catch (JsonException) { /* 非法 JSON，忽略 */ }
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }

    // 主循环结束（客户端发 Close 帧 / 出错 / shell 已死）：无论哪种，都要取消转发任务，
    // 否则它会一直挂在 ReadAllAsync 上，这个请求也就永远 await 不完。
    try { linked.Cancel(); } catch { }
    try { await pump; } catch { }
    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }

    return Results.Empty;
});

// 向交互终端写输入（按键/粘贴/控制键都走这里）
// 保留供向后兼容；前端已改用 WebSocket 双向通道。
app.MapPost("/api/terminal/input", (TerminalInputRequest req, ConnectionManager mgr) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        lock (s.ShellLock)
        {
            if (s.Shell is null || !s.ShellAlive)
                return Results.BadRequest(new { error = "终端未打开" });
            s.Shell.Write(req.Data ?? "");
            s.Shell.Flush();
        }
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 关闭交互终端
app.MapPost("/api/terminal/close", (IdRequest req, ConnectionManager mgr) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        s.DisposeShell();
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 同步会话工作目录（文件列表导航时调用，让终端下一条命令从该目录开始）
app.MapPost("/api/cwd", (CwdRequest req, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(req.ConnectionId);
        if (!string.IsNullOrWhiteSpace(req.Path)) s.Cwd = req.Path.Trim();
        log.Log("info", req.ConnectionId, "切换目录", s.Cwd);
        return Results.Ok(new { path = s.Cwd });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 实时操作日志（Server-Sent Events）
app.MapGet("/api/logs/stream", async (HttpContext ctx, OperationLogger log) =>
{
    ctx.Response.Headers.Append("Content-Type", "text/event-stream");
    ctx.Response.Headers.Append("Cache-Control", "no-cache");
    ctx.Response.Headers.Append("Connection", "keep-alive");
    ctx.Response.Headers.Append("X-Accel-Buffering", "no");

    // 客户端断开或服务停机（shutdownCts）都会取消本循环，否则停机会被这个长连接拖住
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, shutdownCts.Token);

    // 先回放最近若干条，避免新客户端看不到历史
    foreach (var e in log.Recent)
        await WriteLogEvent(ctx, e, cts.Token);
    await ctx.Response.Body.FlushAsync();

    try
    {
        await foreach (var e in log.Stream.WithCancellation(cts.Token))
        {
            await WriteLogEvent(ctx, e, cts.Token);
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { /* 客户端断开 / 服务停机 */ }
});

// 健康检查（顺带返回单文件上传上限，前端据此预校验；maxUploadBytes = 0 表示不限制；
// desktop = 是否运行在桌面壳里，前端据此走原生保存对话框等桌面专属交互）
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    version = AppVersion(),
    maxUploadBytes = maxUploadMb > 0 ? maxUploadMb * 1024 * 1024L : 0,
    desktop = Environment.GetEnvironmentVariable("HXSFM_DESKTOP") == "1",
}));

// ---- 用户偏好设置：常用目录收藏 + 终端宏（Data/settings.json）----
app.MapGet("/api/settings/favorites", (SettingsStore store) =>
    Results.Ok(new { favorites = store.ListFavorites() }));

app.MapPut("/api/settings/favorites", (List<FavoriteDir> favorites, SettingsStore store) =>
{
    store.ReplaceFavorites(favorites);
    return Results.Ok(new { favorites = store.ListFavorites() });
});

app.MapGet("/api/settings/macros", (SettingsStore store) =>
    Results.Ok(new { macros = store.ListMacros() }));

app.MapPut("/api/settings/macros", (List<TerminalMacro> macros, SettingsStore store) =>
{
    store.ReplaceMacros(macros);
    return Results.Ok(new { macros = store.ListMacros() });
});

// ---- 命令历史：Terminal 执行过的命令，双击可再次执行 ----
app.MapGet("/api/settings/history", (SettingsStore store) =>
    Results.Ok(new { history = store.ListHistory() }));

app.MapPost("/api/settings/history", (AddHistoryRequest req, SettingsStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.ConnKey) || string.IsNullOrWhiteSpace(req.Command))
        return Results.BadRequest(new { error = "缺少连接标识或命令" });
    store.AppendHistory(new CommandHistoryItem(
        req.ConnKey,
        req.Command.Trim(),
        req.Cwd ?? "",
        req.ExitStatus,
        DateTime.Now));
    return Results.Ok(new { ok = true });
});

// 清空指定连接的命令历史（connKey 为空则清空全部）
app.MapDelete("/api/settings/history", (string? connKey, SettingsStore store) =>
{
    store.ClearHistory(connKey);
    return Results.Ok(new { ok = true });
});

// ---- 服务器状态：系统版本 / 开机时间 / CPU / 内存 / 磁盘 / 网络 ----
// 一次 exec 通道内打包所有采集命令（SSH.NET 每次 CreateCommand 开新通道，
// 拆开多调会争用同一会话），远程脚本输出带 ===SECTION=== 分隔，后端解析成结构化 JSON。
app.MapGet("/api/system-status", (string connId, ConnectionManager mgr) =>
{
    try
    {
        var s = mgr.Get(connId);
        using var cmd = s.Ssh.CreateCommand(SystemStatusHelpers.Script);
        cmd.CommandTimeout = TimeSpan.FromSeconds(20);
        cmd.Execute();
        var status = SystemStatusHelpers.Parse((cmd.Result ?? "").Replace("\r\n", "\n"));
        SystemStatusHelpers.ApplyRates(connId, status.Nets);
        return Results.Ok(status);
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// ---- 实时网络上下行（SSE，MobaXterm 风格）----
// 一条常驻 exec 通道里每 interval 秒 `cat /proc/net/dev`，后端按相邻两次快照算瞬时速率推给前端。
// 不每秒新开 SSH 通道（会争用会话 + 每次都要重新握手），也不依赖远端 awk / /sys。
var netJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
app.MapGet("/api/net-stream", async (string connId, HttpContext ctx, ConnectionManager mgr, int? interval) =>
{
    SshSession s;
    try { s = mgr.Get(connId); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

    var sec = Math.Clamp(interval ?? 1, 1, 10);

    ctx.Response.Headers.Append("Content-Type", "text/event-stream");
    ctx.Response.Headers.Append("Cache-Control", "no-cache");
    ctx.Response.Headers.Append("Connection", "keep-alive");
    ctx.Response.Headers.Append("X-Accel-Buffering", "no");

    // 客户端断开或服务停机（shutdownCts）都会取消本循环，否则停机会被这个长连接拖住
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, shutdownCts.Token);

    using var cmd = s.Ssh.CreateCommand(SystemStatusHelpers.NetStreamScript(sec));
    cmd.CommandTimeout = Timeout.InfiniteTimeSpan;
    cmd.BeginExecute();

    // PipeStream 的 Read 是阻塞的、取消令牌管不着，所以放后台线程逐行读进有界 Channel；
    // 退出时 using cmd 释放通道 → 阻塞的 Read 返回 → 读取线程自然结束（远端 while 循环随通道关闭终止）
    var lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });
    _ = Task.Run(() =>
    {
        try
        {
            using var sr = new StreamReader(cmd.OutputStream, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null) lines.Writer.TryWrite(line);
        }
        catch { /* 通道释放 / SSH 断开 */ }
        finally { lines.Writer.TryComplete(); }
    });

    var block = new List<string>();
    List<NetStatus>? prev = null;
    DateTime prevTs = default;
    try
    {
        await foreach (var line in lines.Reader.ReadAllAsync(cts.Token))
        {
            if (!line.StartsWith(SystemStatusHelpers.NetTickMarker, StringComparison.Ordinal))
            {
                if (block.Count < 512) block.Add(line);
                continue;
            }

            var now = DateTime.UtcNow;
            var cur = SystemStatusHelpers.ParseNetDev(block);
            block.Clear();
            var dt = prevTs == default ? 0 : (now - prevTs).TotalSeconds;
            prevTs = now;
            var nets = SystemStatusHelpers.WithRates(cur, prev, dt);
            prev = nets;
            s.Touch(); // 看状态期间不算空闲，防 30 分钟空闲回收把会话掐掉

            var payload = new
            {
                intervalSec = sec,
                warmup = dt <= 0, // 首帧只有累计字节、没有速率
                nets,
                totalRxBps = nets.Sum(n => n.RxRateBps),
                totalTxBps = nets.Sum(n => n.TxRateBps),
            };
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, netJson)}\n\n", cts.Token);
            await ctx.Response.Body.FlushAsync(cts.Token);
        }
    }
    catch (OperationCanceledException) { /* 客户端断开 / 服务停机 */ }
    catch (Exception) { /* 会话断开等：静默收尾，前端 EventSource 会自己重连 */ }
    // 响应已开始，不能返回 Results.Ok()（重复设状态码会抛）；Empty 不触碰状态码
    return Results.Empty;
});

Console.WriteLine($"[HxServerFileManager] 版本 {AppVersion()}");
Console.WriteLine($"[HxServerFileManager] Kestrel 已启动，监听 http://0.0.0.0:{listenPort}");
Console.WriteLine("[HxServerFileManager] 按 Ctrl+C 停止服务");
        return app;
    }

    // 应用版本号：读取程序集 InformationalVersion（csproj 的 <Version>），
    // 去掉可能附加的 +git哈希 后缀只留纯版本号。桌面壳标题也读这里，保证两边一致。
    public static string AppVersion()
    {
        var asm = typeof(WebHost).Assembly;
        var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) info = asm.GetName().Version?.ToString() ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info.Substring(0, plus) : info;
    }
}

// ----------------------------------------------------------------------------
// 辅助函数：WebHost.Build 内的路由 lambda 调用，放在 partial class WebHost 中
// ----------------------------------------------------------------------------

public static partial class WebHost
{
    internal static string CombinePath(string dir, string name)
    {
        dir = (dir ?? "/").TrimEnd('/');
        if (dir == "") dir = "";
        return dir + "/" + name.TrimStart('/');
    }

    internal static string ParentDir(string p)
    {
        p = (p ?? "/").TrimEnd('/');
        if (p == "") return "/";
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    // 递归创建远端目录（已存在则跳过）—— 上传文件夹时目标目录/中间目录可能还不存在。
    // remoteDir 必须是绝对路径（CombinePath 产物），逐段累积并补齐缺失层级；已存在时幂等跳过
    internal static void EnsureRemoteDir(SshSession s, string remoteDir)
    {
        if (string.IsNullOrWhiteSpace(remoteDir)) return;
        var cur = "";
        foreach (var seg in remoteDir.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur += "/" + seg;
            if (!s.Sftp.Exists(cur))
                s.Sftp.CreateDirectory(cur);
        }
    }

    // 单引号转义，用于安全地把路径拼进 sh 命令
    internal static string Shq(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // scp 把 user@host: 之后的路径交给远端 shell 解析：对非安全字符逐个反斜杠转义（空格/引号/$ 等），
    // 拼接成 user@host:path 后整体再 Shq 一层供源机本地 shell 展开，两层处理保证特殊字符路径也能工作。
    internal static string EscapeScpRemote(string path)
    {
        var sb = new StringBuilder(path.Length + 8);
        foreach (var ch in path)
        {
            if (char.IsLetterOrDigit(ch) || ch is '/' or '.' or '-' or '_' or '~')
                sb.Append(ch);
            else
                sb.Append('\\').Append(ch);
        }
        return sb.ToString();
    }

    // ----------------------------------------------------------------------------
    // 鉴权辅助（HxSimpleWebAuth）
    // ----------------------------------------------------------------------------

    /// <summary>
    /// 读取 configs/env.json 中的 maxUploadMb（模板见 configs/env.json.example）。
    /// 0 或负数 = 不限制（Kestrel 不设请求体上限、health 返回 0）；
    /// 文件不存在 / 未配置 / 解析失败返回 null（此时回退到环境变量/默认值）。
    /// </summary>
    internal static int? LoadConfigMaxUploadMb(string contentRoot)
    {
        var configPath = Path.Combine(contentRoot, "configs", "env.json");
        if (!File.Exists(configPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("maxUploadMb", out var p) && p.TryGetInt32(out var mb))
                return mb;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HxServerFileManager] 读取 configs/env.json 失败：{ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 读取 configs/env.json 中的 authPwd（模板见 configs/env.json.example）。
    /// 文件不存在或解析失败返回 null（此时回退到环境变量/未配置）。
    /// </summary>
    internal static string? LoadConfigPassword(string contentRoot)
    {
        var configPath = Path.Combine(contentRoot, "configs", "env.json");
        if (!File.Exists(configPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("authPwd", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var v = p.GetString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HxServerFileManager] 读取 configs/env.json 失败：{ex.Message}");
        }
        return null;
    }

    internal static bool IsLoopbackAddress(System.Net.IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return System.Net.IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// 把 HttpContext 转成 HxSimpleWebAuth 需要的 HttpRequestData。
    /// EventSource/<a download> 等场景带不了请求头，token 走 ?token= 查询参数，
    /// 这里统一补成 Authorization: Bearer 头再交给库校验。
    /// </summary>
    internal static HttpRequestData CreateAuthRequest(HttpContext context, string body = "", string? method = null)
    {
        var headers = context.Request.Headers.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        if (!headers.ContainsKey("Authorization")
            && context.Request.Query.TryGetValue("token", out var qtoken)
            && !string.IsNullOrWhiteSpace(qtoken.ToString()))
        {
            headers["Authorization"] = "Bearer " + qtoken.ToString();
        }

        var target = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        // 本机访问时统一记为 127.0.0.1：HxSimpleWebAuth 会把 token 绑定到登录时的 RemoteIp，
        // 浏览器在 localhost / 127.0.0.1 / [::1] 间漂移会让同一段 token 因来源 IP 不同被判失效
        // （表现为"登录成功点一下就过期"）。回环本就是同一来源，归一后不再互相失效。
        var rawIp = context.Connection.RemoteIpAddress;
        string remoteIp = rawIp is null
            ? "unknown"
            : System.Net.IPAddress.IsLoopback(rawIp) ? "127.0.0.1" : rawIp.ToString();
        return new HttpRequestData(
            (method ?? context.Request.Method).ToUpperInvariant(),
            target,
            headers,
            body,
            remoteIp);
    }

    internal static async Task<HttpRequestData> CreateAuthRequestAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;
        return CreateAuthRequest(context, body);
    }

    internal static async Task WriteAuthResponseAsync(HttpContext context, ApiResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        if (response.AllowHeader is not null)
            context.Response.Headers.Allow = response.AllowHeader;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = response.Body.Length;
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }

    internal static async Task SendWsJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    // 真正执行连接（纯逻辑，不含持久化/日志，便于 connect 与 reconnect 复用）
    internal static (bool ok, string? connectionId, string? home, string? error) ConnectInternal(ConnectRequest req, ConnectionManager mgr)
    {
        try
        {
            var port = req.Port is int p && p > 0 ? p : 22;
            var authMethods = new List<AuthenticationMethod>();

            if (!string.IsNullOrWhiteSpace(req.PrivateKey))
            {
                try
                {
                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(req.PrivateKey));
                    var keyFile = new PrivateKeyFile(ms, req.Passphrase);
                    authMethods.Add(new PrivateKeyAuthenticationMethod(req.Username, keyFile));
                }
                catch (Exception ex)
                {
                    return (false, null, null, "私钥解析失败: " + ex.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(req.Password))
                authMethods.Add(new PasswordAuthenticationMethod(req.Username, req.Password));

            if (authMethods.Count == 0)
                return (false, null, null, "必须提供密码或私钥");

            var connInfo = new Renci.SshNet.ConnectionInfo(req.Host, port, req.Username, authMethods.ToArray());
            var ssh = new SshClient(connInfo);
            var sftp = new SftpClient(connInfo);
            // 心跳：拔网线/服务端假死这类「没有 FIN 的断开」靠 TCP 自身要等很久才暴露，
            // 定期发 keepalive 让 SSH.NET 尽快把连接判死并触发 ErrorOccurred。
            ssh.KeepAliveInterval = TimeSpan.FromSeconds(20);
            sftp.KeepAliveInterval = TimeSpan.FromSeconds(30);
            ssh.Connect();
            sftp.Connect();

            string home = "/";
            try { home = sftp.WorkingDirectory; } catch { /* ignore */ }

            var session = new SshSession(ssh, sftp, home)
            {
                Host = req.Host,
                Port = port,
                Username = req.Username,
                Password = req.Password,
                PrivateKey = req.PrivateKey,
                Passphrase = req.Passphrase,
            };
            // SSH 层异常（对端主动断开 / 网络中断 / 心跳超时）时标记会话失活并关闭 shell，
            // 让正在读 shell 输出的终端 WebSocket 能尽快结束、前端显示「连接已断开」提示。
            ssh.ErrorOccurred += (_, _) =>
            {
                if (!ssh.IsConnected) session.MarkBroken();
            };
            var id = mgr.Add(session);
            return (true, id, home, null);
        }
        catch (Exception ex)
        {
            return (false, null, null, "连接失败: " + ex.Message);
        }
    }

    internal static ConnectionProfile ToProfile(ConnectRequest req, int port) => new(
        Id: Guid.NewGuid().ToString("N"),
        Name: req.Name ?? req.Host,
        Host: req.Host,
        Port: port,
        Username: req.Username,
        AuthType: string.IsNullOrWhiteSpace(req.PrivateKey) ? "password" : "key",
        Password: req.Password,
        PrivateKey: req.PrivateKey,
        Passphrase: req.Passphrase,
        CreatedAt: DateTime.Now,
        LastConnectedAt: DateTime.Now);

    internal static async Task WriteLogEvent(HttpContext ctx, LogEntry e, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(e);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
    }
}

/// <summary>
/// 文本文件识别辅助（扩展名启发式 + 大小限制）。
/// </summary>
public static class FileHelpers
{
    public const long MaxEditBytes = 10 * 1024 * 1024; // 10MB

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".conf", ".config", ".ini", ".cfg", ".prop", ".properties",
        ".json", ".yaml", ".yml", ".xml", ".toml", ".env",
        ".html", ".htm", ".css", ".js", ".mjs", ".jsx", ".ts", ".tsx", ".vue",
        ".py", ".sh", ".bash", ".zsh", ".ps1", ".rb", ".php", ".go", ".cs", ".c", ".cpp",
        ".h", ".hpp", ".hxx", ".java", ".kt", ".rs", ".sql", ".md", ".markdown",
        ".csv", ".tsv", ".pl", ".lua", ".r", ".swift", ".scala", ".gradle",
        ".gitignore", ".dockerfile", ".lock", ".sed", ".awk", ".tex", ".asm",
        ".bat", ".cmd", ".makefile", ".cmake", ".editorconfig"
    };

    public static bool LooksText(string name, long size)
    {
        if (size < 0 || size > MaxEditBytes) return false;
        var ext = Path.GetExtension(name);
        return TextExtensions.Contains(ext);
    }
}

public record ConnectRequest(
    string Host,
    int? Port,
    string Username,
    string? Password,
    string? PrivateKey,
    string? Passphrase,
    string? Name = null);

public record IdRequest(string ConnectionId);
public record PathRequest(string ConnectionId, string Path, string Name);
public record EnsureDirsRequest(string ConnectionId, string Path, string[]? Dirs);
public record RenameRequest(string ConnectionId, string Path, string Name, string NewPath);
public record CommandRequest(string ConnectionId, string Command);
public record ServerCopyRequest(string SourceConnId, string TargetConnId, string[]? Items, string TargetDir);
public record DownloadManyRequest(string? ConnectionId, string[]? Paths);
public record ServerCopyItemState(string Path, string State, string? Message);
public record FileContentRequest(string ConnectionId, string Path, string Content);
public record CwdRequest(string ConnectionId, string Path);
public record TerminalOpenRequest(string ConnectionId, int? Cols, int? Rows);
public record TerminalInputRequest(string ConnectionId, string Data);

public record FileEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime LastWriteTimeUtc, bool IsText);

public record LogEntry(DateTime Time, string Level, string Connection, string Action, string Detail, string? Result);

// ---- 用户偏好设置：常用目录（收藏）+ 终端宏 + 命令历史 ----
// ConnKey = 连接稳定标识（前端：已保存连接用 profileId，未保存用 username@host:port）。
// connectionId 每次重连都会变，不能当持久键；ConnKey 保证收藏/宏/历史跨重连仍归属同一台服务器。
public record FavoriteDir(string Id, string ConnectionId, string ConnKey, string Name, string Path, DateTime CreatedAt, DateTime UpdatedAt);
public record TerminalMacro(string Id, string ConnKey, string Name, string Command, DateTime CreatedAt, DateTime UpdatedAt);
// 命令历史：Terminal 里实际执行过的命令（快捷命令一次一条；交互终端按回车切分），双击可再次执行。
// Cwd = 命令执行时所在目录（快捷命令模式有值；交互终端未知时为 null/空）。
public record CommandHistoryItem(string ConnKey, string Command, string Cwd, int ExitStatus, DateTime CreatedAt);
public record AddHistoryRequest(string? ConnKey, string? Command, string? Cwd, int ExitStatus);
public record UserSettings(List<FavoriteDir> Favorites, List<TerminalMacro> Macros, List<CommandHistoryItem> History);

/// <summary>
/// 一个到 Linux 服务器的 SSH/SFTP 会话。
/// 注意：SSH.NET 客户端并非线程安全，并发操作同一会话需自行节制。
/// </summary>
public sealed class SshSession : IDisposable
{
    public SshClient Ssh { get; }
    public SftpClient Sftp { get; }
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>当前工作目录：命令包装 + 文件列表联动共用的会话级状态。</summary>
    public string Cwd { get; set; }

    // ---- 连接元数据（服务器间直传 / 日志标签用；凭据仅在内存，与 ConnectionsStore 一致）----
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public string? Passphrase { get; set; }

    // ---- 交互终端（ShellStream + pty）----
    public object ShellLock { get; } = new();
    public ShellStream? Shell { get; set; }
    public bool ShellAlive { get; set; }
    public Channel<byte[]>? ShellOutput { get; set; }
    public StringBuilder ShellTail { get; } = new(); // 新 SSE 消费者回放

    public SshSession(SshClient ssh, SftpClient sftp, string cwd)
    {
        Ssh = ssh;
        Sftp = sftp;
        Cwd = cwd;
    }

    /// <summary>惰性创建交互式 shell（带 pty，可跑 nano/vim/top 等需要 TTY 的程序）。</summary>
    public ShellStream EnsureShell(uint cols, uint rows)
    {
        lock (ShellLock)
        {
            if (ShellAlive && Shell != null) return Shell;
            var stream = Ssh.CreateShellStream("xterm-256color", cols, rows, cols * 8, rows * 16, 8192);
            Shell = stream;
            ShellAlive = true;
            // 有界+丢写：没有 SSE 消费者时（如长时间没人看）不会无限积压
            ShellOutput = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4000)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite
            });
            stream.DataReceived += OnShellData;
            // 注入 bash 提示符钩子：每次打印提示符前输出 OSC 7 序列携带当前目录
            // （\033]7;file://host/path\007，VSCode/tmux 的标准做法），前端解析后同步文件列表
            try
            {
                var init =
                    "export PROMPT_COMMAND='printf \"\\033]7;file://%s%s\\007\" \"$HOSTNAME\" \"$PWD\"'\n" +
                    "export PS1='\\u@\\h:\\w$ '\n";
                stream.Write(init);
                stream.Flush();
            }
            catch { /* 非 bash 或写入失败：仅失去路径同步，不影响终端使用 */ }
            return stream;
        }
    }

    private void OnShellData(object? sender, ShellDataEventArgs e)
    {
        if (e.Data is not { Length: > 0 } data) return;
        lock (ShellTail)
        {
            ShellTail.Append(Encoding.UTF8.GetString(data));
            if (ShellTail.Length > 65536) ShellTail.Remove(0, ShellTail.Length - 65536);
        }
        ShellOutput?.Writer.TryWrite(data);
    }

    public void DisposeShell()
    {
        lock (ShellLock)
        {
            if (Shell != null)
            {
                try { Shell.DataReceived -= OnShellData; } catch { }
                try { Shell.Close(); } catch { }
                try { Shell.Dispose(); } catch { }
            }
            Shell = null;
            ShellAlive = false;
            // 先完成 channel：正在 ReadAllAsync 的终端 WebSocket 转发任务会立刻结束，
            // ws 随之关闭，前端 onclose 触发「连接已断开」提示（否则会一直挂着等数据）。
            try { ShellOutput?.Writer.TryComplete(); } catch { }
            ShellOutput = null;
            lock (ShellTail) ShellTail.Clear();
        }
    }

    public void Touch() => LastUsedUtc = DateTime.UtcNow;

    public void Dispose()
    {
        DisposeShell();
        try { if (Sftp.IsConnected) Sftp.Disconnect(); } catch { }
        try { if (Ssh.IsConnected) Ssh.Disconnect(); } catch { }
        Sftp.Dispose();
        Ssh.Dispose();
    }

    /// <summary>SSH/SFTP 连接异常时调用：关闭 shell，让所有消费 ShellOutput 的终端 WebSocket 立即结束，前端得以显示「连接已断开」。</summary>
    public void MarkBroken()
    {
        DisposeShell();
        try { if (Sftp.IsConnected) Sftp.Disconnect(); } catch { }
        try { if (Ssh.IsConnected) Ssh.Disconnect(); } catch { }
    }
}

/// <summary>
/// 内存会话表：连接Id -> SSH/SFTP 会话。
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, SshSession> _sessions = new();
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(30);

    public string Add(SshSession session)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = session;
        return id;
    }

    public SshSession Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_sessions.TryGetValue(id, out var s))
            throw new InvalidOperationException("连接不存在或已断开，请重新连接。");
        s.Touch();
        return s;
    }

    // 不抛异常地取出（用于日志标签）
    public SshSession? GetSilently(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : _sessions.GetValueOrDefault(id);

    public void Remove(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (_sessions.TryRemove(id, out var s)) s.Dispose();
    }

    /// <summary>停机时关闭全部会话（断开 SSH/SFTP，Shell 输出 channel 完成，终端长连接随之收尾）。</summary>
    public void DisposeAll()
    {
        foreach (var kvp in _sessions)
        {
            if (_sessions.TryRemove(kvp.Key, out var s)) s.Dispose();
        }
    }

    /// <summary>列出所有活跃会话的存活状态（供前端轮询，发现断开后提示并可重连）。</summary>
    public List<object> ListHealth()
    {
        var list = new List<object>();
        foreach (var kvp in _sessions)
        {
            var s = kvp.Value;
            try
            {
                // Ssh.IsConnected 是本地握手后的乐观标志；IsRunning 表示底层消息循环仍在。
                // 两者任一为 false 说明对端断开或会话已失效。
                var alive = s.Ssh.IsConnected && s.Sftp.IsConnected;
                list.Add(new { connectionId = kvp.Key, connected = alive });
            }
            catch
            {
                list.Add(new { connectionId = kvp.Key, connected = false });
            }
        }
        return list;
    }

    public async Task CleanupLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), token);
                var now = DateTime.UtcNow;
                foreach (var kvp in _sessions)
                {
                    if (now - kvp.Value.LastUsedUtc > _idleTimeout)
                    {
                        if (_sessions.TryRemove(kvp.Key, out var s)) s.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 退出 */ }
    }
}

/// <summary>
/// 服务器间直传任务：在源服务器上执行 scp 把选中项复制到目标服务器，数据全程在两端服务器之间
/// 流动（A -> B），本机只下发指令、轮询每项状态，不做任何字节中转。
/// </summary>
public sealed class ServerCopyJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string SourceLabel { get; }
    public string TargetLabel { get; }
    public string[] Items { get; }
    public string TargetDir { get; }
    public int Total => Items.Length;
    public int Done { get; private set; }
    public string State { get; private set; } = "running"; // running | done | failed
    public string? Error { get; private set; }
    public List<ServerCopyItemState> ItemStates { get; } = new();
    public DateTime CreatedAt { get; } = DateTime.Now;
    public DateTime? FinishedAt { get; private set; }

    public ServerCopyJob(string sourceLabel, string targetLabel, string[] items, string targetDir)
    {
        SourceLabel = sourceLabel;
        TargetLabel = targetLabel;
        Items = items;
        TargetDir = targetDir;
        foreach (var p in items) ItemStates.Add(new ServerCopyItemState(p, "pending", null));
    }

    public void MarkRunning(int index) => ItemStates[index] = ItemStates[index] with { State = "running" };

    public void MarkDone(int index)
    {
        ItemStates[index] = ItemStates[index] with { State = "done" };
        Done++;
    }

    public void FinishOk()
    {
        State = "done";
        FinishedAt = DateTime.Now;
    }

    public void FinishFailed(int index, string message)
    {
        ItemStates[index] = ItemStates[index] with { State = "failed", Message = message };
        State = "failed";
        Error = message;
        FinishedAt = DateTime.Now;
    }

    public void FailEarly(string message)
    {
        State = "failed";
        Error = message;
        FinishedAt = DateTime.Now;
    }
}

/// <summary>
/// 服务器间直传作业的注册表 + 后台执行。
/// 认证策略（目标机）：
///   密码认证 → 源机装 sshpass 时用 sshpass 喂密码；未装时先试免密 scp（两端已配密钥时直接可用），
///             失败时错误信息引导安装 sshpass 或配置密钥互信；
///   私钥认证 → 把私钥内容（base64 编码经 shell 写盘）临时放到源机 /tmp，scp -i 使用，结束即删；
///             带口令的私钥无法非交互使用，直接给出明确错误。
/// </summary>
public static class ServerCopyJobs
{
    private static readonly ConcurrentDictionary<string, ServerCopyJob> Jobs = new();

    public static ServerCopyJob Add(ServerCopyJob job)
    {
        Jobs[job.Id] = job;
        Purge();
        return job;
    }

    public static ServerCopyJob? Get(string id)
        => string.IsNullOrWhiteSpace(id) ? null : Jobs.GetValueOrDefault(id);

    // 完成超过 1 小时的作业清理掉，避免长期占用内存
    private static void Purge()
    {
        var cutoff = DateTime.Now.AddHours(-1);
        foreach (var kvp in Jobs)
        {
            if (kvp.Value.State != "running" && kvp.Value.FinishedAt is DateTime t && t < cutoff)
                Jobs.TryRemove(kvp.Key, out _);
        }
    }

    public static void Start(ServerCopyJob job, SshSession src, SshSession dst, OperationLogger log)
        => _ = Task.Run(() => RunAsync(job, src, dst, log));

    private static async Task RunAsync(ServerCopyJob job, SshSession src, SshSession dst, OperationLogger log)
    {
        string? keyFile = null;
        try
        {
            var auth = ResolveAuth(job, src, dst, ref keyFile);
            if (auth is null) return; // ResolveAuth 失败时已 FailEarly

            var baseOpts = "-r -P " + dst.Port +
                           " -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null" +
                           " -o ConnectTimeout=15 -o NumberOfPasswordPrompts=1";
            // 目标目录补尾斜杠表示「复制到目录内」（scp 靠尾斜杠区分覆盖 vs 放进目录）；
            // 远端路径交给目标机 shell 解析，先逐个反斜杠转义，再整体 Shq 供源机本地 shell 展开。
            var remoteSpec = WebHost.Shq($"{dst.Username}@{dst.Host}:{WebHost.EscapeScpRemote(job.TargetDir.TrimEnd('/') + "/")}");

            for (var i = 0; i < job.Items.Length; i++)
            {
                src.Touch(); // 长时间传输期间避免会话被空闲回收
                var item = job.Items[i];
                job.MarkRunning(i);
                log.Log("info", job.SourceLabel, "服务器直传", $"{item} -> {job.TargetLabel}:{job.TargetDir}", "进行中");

                var cmdLine = auth switch
                {
                    "key" => $"scp {baseOpts} -i {WebHost.Shq(keyFile!)} {WebHost.Shq(item)} {remoteSpec}",
                    "password-sshpass" => $"sshpass -p {WebHost.Shq(dst.Password!)} scp {baseOpts} {WebHost.Shq(item)} {remoteSpec}",
                    _ => $"scp {baseOpts} {WebHost.Shq(item)} {remoteSpec}", // 密码但无 sshpass：先试两端是否已配免密
                };

                using var cmd = src.Ssh.CreateCommand(cmdLine);
                cmd.CommandTimeout = TimeSpan.FromHours(2);
                cmd.Execute();
                var output = ((cmd.Result ?? "") + (cmd.Error ?? "")).Trim();
                if (cmd.ExitStatus == 0)
                {
                    job.MarkDone(i);
                    log.Log("info", job.SourceLabel, "服务器直传", $"{item} -> {job.TargetLabel}:{job.TargetDir}", "完成");
                    continue;
                }

                var msg = ClassifyError(output, auth, dst);
                log.Log("error", job.SourceLabel, "服务器直传", $"{item} -> {job.TargetLabel}:{job.TargetDir}", msg);
                job.FinishFailed(i, msg);
                return;
            }
            job.FinishOk();
            log.Log("info", job.SourceLabel, "服务器直传", $"{job.Total} 项 -> {job.TargetLabel}:{job.TargetDir}", "全部完成");
        }
        catch (Exception ex)
        {
            job.FailEarly("传输异常：" + ex.Message);
            log.Log("error", job.SourceLabel, "服务器直传", job.TargetDir, ex.Message);
        }
        finally
        {
            // 清理临时私钥文件
            if (keyFile != null)
            {
                try { using var rm = src.Ssh.CreateCommand($"rm -f {WebHost.Shq(keyFile)}"); rm.Execute(); } catch { /* 忽略 */ }
            }
        }
    }

    // 返回 auth："key" | "password-sshpass" | "password-direct"；失败返回 null（已 FailEarly 并记录错误）
    private static string? ResolveAuth(ServerCopyJob job, SshSession src, SshSession dst, ref string? keyFile)
    {
        if (!string.IsNullOrEmpty(dst.Password))
        {
            try
            {
                using var chk = src.Ssh.CreateCommand("command -v sshpass >/dev/null 2>&1 && echo yes || echo no");
                chk.CommandTimeout = TimeSpan.FromSeconds(15);
                chk.Execute();
                return chk.Result.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
                    ? "password-sshpass"
                    : "password-direct";
            }
            catch { return "password-direct"; } // 探测失败按无 sshpass 处理
        }

        if (!string.IsNullOrEmpty(dst.PrivateKey))
        {
            if (!string.IsNullOrEmpty(dst.Passphrase))
            {
                job.FailEarly("目标连接使用带口令的私钥，暂不支持服务器间直传。请改用密码认证，或先去除私钥口令。");
                return null;
            }
            keyFile = $"/tmp/hxsfm_key_{Guid.NewGuid():N}";
            // 私钥内容 base64 编码后经 shell 写盘：私钥里的特殊字符经 base64 后只剩 [A-Za-z0-9+/=]，天然安全
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(dst.PrivateKey));
            using var wr = src.Ssh.CreateCommand($"echo {WebHost.Shq(b64)} | base64 -d > {WebHost.Shq(keyFile)} && chmod 600 {WebHost.Shq(keyFile)}");
            wr.CommandTimeout = TimeSpan.FromSeconds(30);
            wr.Execute();
            if (wr.ExitStatus != 0)
            {
                job.FailEarly("无法把目标私钥写入源服务器临时文件：" + ((wr.Error ?? "") + (wr.Result ?? "")).Trim());
                return null;
            }
            return "key";
        }

        job.FailEarly("目标连接缺少凭据（无密码且无私钥），无法从源服务器发起传输。");
        return null;
    }

    private static string ClassifyError(string output, string auth, SshSession dst)
    {
        var o = output;
        if (Regex.IsMatch(o, @"permission denied|denied, please try|authentication failed|no supported authentication|password.*required|keyboard-interactive", RegexOptions.IgnoreCase))
        {
            if (auth == "password-direct")
                return "源服务器无法免密登录目标机，且未安装 sshpass。请在源服务器安装 sshpass（apt install sshpass / yum install sshpass），或预先配置两台服务器间的 SSH 密钥互信。原始输出：" + o;
            return $"目标机认证失败：请确认目标连接 {dst.Username}@{dst.Host}:{dst.Port} 的凭据正确。原始输出：" + o;
        }
        if (o.Contains("command not found", StringComparison.OrdinalIgnoreCase) || o.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase))
            return "源服务器缺少 scp/sshpass 或路径不存在：" + o;
        if (o.Contains("timed out", StringComparison.OrdinalIgnoreCase) || o.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
            return $"源服务器无法连通目标 {dst.Host}:{dst.Port}（网络隔离或防火墙拦截）：" + o;
        return "复制失败：" + (string.IsNullOrWhiteSpace(o) ? "未知错误（请查看操作日志）" : o);
    }
}

/// <summary>
/// 操作日志：通过 Channel 做实时分发，并保留最近 N 条供新客户端回放。
/// </summary>
public sealed class OperationLogger
{
    private readonly Channel<LogEntry> _channel = Channel.CreateUnbounded<LogEntry>();
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private const int MaxRecent = 500;

    public void Log(string level, string connection, string action, string detail, string? result = null)
    {
        var entry = new LogEntry(DateTime.Now, level, connection, action, detail, result);
        _recent.Enqueue(entry);
        while (_recent.Count > MaxRecent) _recent.TryDequeue(out _);
        _channel.Writer.TryWrite(entry);
    }

    public IReadOnlyList<LogEntry> Recent => _recent.ToArray();
    public IAsyncEnumerable<LogEntry> Stream => _channel.Reader.ReadAllAsync();
}

/// <summary>
/// connections.json 落盘加密：AES-GCM（.NET 内置 AesGcm，带认证防篡改）。
/// 密钥来源（优先前者）：
///   ① 环境变量 HXSFM_DATA_KEY —— 任意长度，SHA-256 派生 32 字节密钥，密钥不进盘（systemd/Docker 注入）
///   ② Data/secret.key —— 首启生成的随机 32 字节密钥文件（Unix 下 chmod 600）
/// 文件格式：前缀 "HXSFM1:" + Base64(nonce(12) || tag(16) || ciphertext)。
/// 老明文文件由 ConnectionsStore 启动时探测，解密迁移为加密落盘。
/// </summary>
public static class DataCrypto
{
    private const string Magic = "HXSFM1:";
    private static byte[]? _key;

    public static byte[] GetKey(string dataDir)
    {
        _key ??= LoadOrCreateKey(dataDir);
        return _key;
    }

    private static byte[] LoadOrCreateKey(string dataDir)
    {
        var env = Environment.GetEnvironmentVariable("HXSFM_DATA_KEY");
        if (!string.IsNullOrWhiteSpace(env))
            return SHA256.HashData(Encoding.UTF8.GetBytes(env));

        var file = Path.Combine(dataDir, "secret.key");
        if (File.Exists(file))
        {
            try
            {
                var k = Convert.FromBase64String(File.ReadAllText(file).Trim());
                if (k.Length is 16 or 24 or 32) return k;
            }
            catch { /* 文件损坏则重新生成 */ }
        }
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(file, Convert.ToBase64String(key));
        try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* 非 Unix 忽略 */ }
        return key;
    }

    public static string Encrypt(string plaintext, byte[] key)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag);
        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, blob, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, blob, nonce.Length + tag.Length, cipher.Length);
        return Magic + Convert.ToBase64String(blob);
    }

    public static bool IsEncrypted(string content) =>
        content.StartsWith(Magic, StringComparison.Ordinal);

    public static string Decrypt(string content, byte[] key)
    {
        var blob = Convert.FromBase64String(content.Substring(Magic.Length));
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var cipher = blob.AsSpan(28);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>
/// 已保存的连接配置（持久化到 Data/connections.json，凭据经 DataCrypto AES-GCM 加密落盘，
/// 仅在内存中以明文存在，供自动重连/导出使用）。
/// </summary>
public sealed class ConnectionsStore
{
    private readonly string _file;
    private readonly object _gate = new();
    private readonly byte[] _key;
    private List<ConnectionProfile> _profiles;

    public ConnectionsStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "connections.json");
        _key = DataCrypto.GetKey(dataDir);
        _profiles = Load();
    }

    private List<ConnectionProfile> Load()
    {
        if (!File.Exists(_file)) return new();
        string content;
        try { content = File.ReadAllText(_file); } catch { return new(); }
        List<ConnectionProfile> list;
        try
        {
            // 大小写不敏感：兼容旧应用写的 PascalCase 文件，也兼容 camelCase（导入/手工编辑的）
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            list = DataCrypto.IsEncrypted(content)
                ? JsonSerializer.Deserialize<List<ConnectionProfile>>(DataCrypto.Decrypt(content, _key), opts) ?? new()
                : JsonSerializer.Deserialize<List<ConnectionProfile>>(content, opts) ?? new();
        }
        catch { return new(); } // 损坏视为无连接，不阻断启动

        // 老明文文件：读取成功后立即加密迁移落盘
        if (!DataCrypto.IsEncrypted(content))
        {
            try { Save(); } catch { /* 迁移失败不阻断，下次保存仍会加密 */ }
        }
        return list;
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_file, DataCrypto.Encrypt(json, _key));
    }

    public IReadOnlyList<ConnectionProfile> List()
    {
        lock (_gate) return _profiles.ToList();
    }

    public ConnectionProfile? Get(string id)
    {
        lock (_gate) return _profiles.FirstOrDefault(p => p.Id == id);
    }

    // 以 host|port|username 去重；已存在则保留原 Id/CreatedAt 并更新凭据与时间。返回最终保存的 profile
    public ConnectionProfile Upsert(ConnectionProfile p)
    {
        lock (_gate)
        {
            var key = $"{p.Host}|{p.Port}|{p.Username}";
            var existing = _profiles.FirstOrDefault(x => $"{x.Host}|{x.Port}|{x.Username}" == key);
            ConnectionProfile saved;
            if (existing is null)
            {
                _profiles.Add(p);
                saved = p;
            }
            else
            {
                _profiles.Remove(existing);
                saved = p with { Id = existing.Id, CreatedAt = existing.CreatedAt };
                _profiles.Add(saved);
            }
            Save();
            return saved;
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var x = _profiles.FirstOrDefault(p => p.Id == id);
            if (x != null) { _profiles.Remove(x); Save(); }
        }
    }

    // 覆盖导入：清空现有连接，整体替换为导入列表
    public void ReplaceAll(IEnumerable<ConnectionProfile> list)
    {
        lock (_gate)
        {
            _profiles = list.ToList();
            Save();
        }
    }

    // 去重合并导入：按 host|port|username|password 四字段全一致判定重复。
    // 重复 → 用导入内容更新（保留原 Id/CreatedAt）；不重复 → 新增。
    public (int added, int updated) MergeImport(IEnumerable<ConnectionProfile> incoming)
    {
        lock (_gate)
        {
            var added = 0;
            var updated = 0;
            foreach (var p in incoming)
            {
                var key = $"{p.Host}|{p.Port}|{p.Username}|{p.Password}";
                var existing = _profiles.FirstOrDefault(x => $"{x.Host}|{x.Port}|{x.Username}|{x.Password}" == key);
                if (existing is null)
                {
                    _profiles.Add(p);
                    added++;
                }
                else
                {
                    _profiles.Remove(existing);
                    _profiles.Add(p with { Id = existing.Id, CreatedAt = existing.CreatedAt });
                    updated++;
                }
            }
            Save();
            return (added, updated);
        }
    }
}

public record ConnectionProfile(
    string Id,
    string Name,
    string Host,
    int Port,
    string Username,
    string AuthType,
    string? Password,
    string? PrivateKey,
    string? Passphrase,
    DateTime CreatedAt,
    DateTime LastConnectedAt);

// 用户偏好设置存储：Data/settings.json（常用目录收藏 + 终端宏），
// 与 ConnectionsStore 同款 JSON 持久化；写时先写临时文件再原子替换，避免中途崩溃损坏 JSON。
public sealed class SettingsStore
{
    private readonly string _file;
    private readonly object _gate = new();
    private UserSettings _settings;

    public SettingsStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "settings.json");
        _settings = Load();
    }

    private static UserSettings Empty() => new(new List<FavoriteDir>(), new List<TerminalMacro>(), new List<CommandHistoryItem>());

    private UserSettings Load()
    {
        if (!File.Exists(_file)) return Empty();
        try
        {
            var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_file)) ?? Empty();
            // 旧版本 settings.json 没有 History 字段（反序列化为 null），统一规整为空列表
            return s with { Favorites = s.Favorites ?? new(), Macros = s.Macros ?? new(), History = s.History ?? new() };
        }
        catch { return Empty(); } // 文件损坏视为无设置，不阻断启动
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _file, overwrite: true);
    }

    public IReadOnlyList<FavoriteDir> ListFavorites()
    {
        lock (_gate) return _settings.Favorites.ToList();
    }

    public IReadOnlyList<TerminalMacro> ListMacros()
    {
        lock (_gate) return _settings.Macros.ToList();
    }

    public void ReplaceFavorites(List<FavoriteDir> favorites)
    {
        lock (_gate)
        {
            _settings = _settings with { Favorites = favorites ?? new List<FavoriteDir>() };
            Save();
        }
    }

    public void ReplaceMacros(List<TerminalMacro> macros)
    {
        lock (_gate)
        {
            _settings = _settings with { Macros = macros ?? new List<TerminalMacro>() };
            Save();
        }
    }

    public IReadOnlyList<CommandHistoryItem> ListHistory()
    {
        lock (_gate) return (_settings.History ?? new List<CommandHistoryItem>()).ToList();
    }

    // 追加一条命令历史：同一连接同一命令只保留最新一条（重复执行时时间戳刷新置顶，类似 shell 的 history）；
    // 每个连接最多保留最近 200 条，超出丢最旧。
    public void AppendHistory(CommandHistoryItem item)
    {
        lock (_gate)
        {
            var list = _settings.History ?? new List<CommandHistoryItem>();
            list = list
                .Where(h => h.ConnKey != item.ConnKey || h.Command != item.Command)
                .Append(item)
                .ToList();
            var perKey = list.Where(h => h.ConnKey == item.ConnKey).ToList();
            if (perKey.Count > 200)
            {
                var drop = perKey.Take(perKey.Count - 200).ToHashSet();
                list = list.Where(h => !(h.ConnKey == item.ConnKey && drop.Contains(h))).ToList();
            }
            _settings = _settings with { History = list };
            Save();
        }
    }

    // 清空命令历史；connKey 为空时清空全部（前端按连接逐个清）。
    public void ClearHistory(string? connKey)
    {
        lock (_gate)
        {
            var list = _settings.History ?? new List<CommandHistoryItem>();
            list = string.IsNullOrEmpty(connKey)
                ? new List<CommandHistoryItem>()
                : list.Where(h => h.ConnKey != connKey).ToList();
            _settings = _settings with { History = list };
            Save();
        }
    }
}

// ---- 服务器状态：远程采集脚本 + 输出解析 ----

public record DiskStatus(string Fs, string Size, string Used, string Avail, string Use, string Mount);
public record NetStatus(string Name, string State, long RxBytes, long TxBytes, double RxRateBps = 0, double TxRateBps = 0);

public record SystemStatus(
    string? Hostname,
    long UnixTs,
    string? Os,
    string? Kernel,
    string? Arch,
    long UptimeSeconds,
    double? CpuPercent,
    long MemTotal, long MemUsed, long MemFree, long MemAvailable, double MemPercent,
    long SwapTotal, long SwapUsed, double SwapPercent,
    List<DiskStatus> Disks,
    List<NetStatus> Nets);

/// <summary>
/// 服务器状态采集：仅在远程执行一段 sh（兼容最小化系统：只有 /proc 和 coreutils 也能跑）。
/// CPU 通过在 /proc/stat 上取前后两次快照（间隔 0.6s）算利用率，不依赖 top 的版本差异。
/// </summary>
public static class SystemStatusHelpers
{
    /// <summary>
    /// 远程采集脚本。**必须用 <see cref="Sh"/> 归一化换行后再发给远端**：本文件是 CRLF，
    /// 逐字发出去时每行结尾都带 \r，远端 bash 把 `2>&amp;1\r` 里的 `&amp;1\r` 当文件名 →
    /// "ambiguous redirect"，整条命令不执行（历史 bug：NET 段因此永远为空）。
    /// </summary>
    public static readonly string Script = Sh(ScriptRaw);

    /// <summary>把脚本里的 CRLF 换成 LF（远端是 sh，\r 会被当命令的一部分）。</summary>
    public static string Sh(string script) => script.Replace("\r\n", "\n");

    private const string ScriptRaw = """
        echo "===META==="
        hostname 2>&1 || echo "unknown"
        date +%s 2>&1 || echo "0"
        echo "===OS==="
        [ -f /etc/os-release ] && head -3 /etc/os-release 2>&1 || echo "no-os-release"
        uname -r 2>&1 || echo "no-kernel"
        uname -m 2>&1 || echo "no-arch"
        echo "===UPTIME==="
        [ -f /proc/uptime ] && head -1 /proc/uptime 2>&1 || echo "0 0"
        echo "===CPU==="
        c1=$(grep '^cpu ' /proc/stat 2>&1 | head -1); sleep 0.6; c2=$(grep '^cpu ' /proc/stat 2>&1 | head -1); echo "$c1"; echo "$c2"
        echo "===MEM==="
        [ -f /proc/meminfo ] && head -8 /proc/meminfo 2>&1 || echo "no-meminfo"
        echo "===DISK==="
        # 排除 docker/containerd 的容器挂载（/var/lib/docker/overlay2/<hash>、/var/lib/docker/containers/<id> 等
        # 每容器一条、数量爆炸且都是同一磁盘重复数据；/var/lib/docker 根挂载本身保留，能看到数据盘真实占用）
        df -h -P 2>&1 | awk 'NR>1 && NF>=6 && $1 !~ /^tmpfs/ && $1 !~ /^devtmpfs/ && $6 !~ /^\/var\/lib\/docker\// && $6 !~ /^\/var\/lib\/containerd\// {m=$6; for(i=7;i<=NF;i++) m=m" "$i; print $1"|"$2"|"$3"|"$4"|"$5"|"m}' || echo "no-df"
        echo "===NET==="
        # 直接从 /proc/net/dev 读全部接口（不依赖 /sys/class/net 目录遍历，容器里可能缺失）；
        # 每接口尝试读 operstate；输出 name|state|rx|tx
        awk 'NR>2 {
          n=$1; sub(/:/,"",n);
          if (n=="lo") next;
          st="unknown";
          f="/sys/class/net/" n "/operstate";
          if ((getline x < f) > 0) { st=x; close(f) }
          print n"|"st"|"$2"|"$10
        }' /proc/net/dev 2>&1
        echo "===END==="
        """;

    public static SystemStatus Parse(string text)
    {
        // 按 ===SECTION=== 分节
        var sections = new Dictionary<string, List<string>>();
        string cur = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("===") && line.EndsWith("===") && line.Length > 6)
            {
                cur = line.Trim('=', ' ', '\r');
                if (cur.Length > 0 && !sections.ContainsKey(cur)) sections[cur] = new List<string>();
            }
            else if (cur.Length > 0 && line.Length > 0)
            {
                sections[cur].Add(line);
            }
        }

        var meta = sections.GetValueOrDefault("META") ?? new List<string>();
        var hostname = meta.FirstOrDefault()?.Trim();
        long unixTs = 0;
        if (meta.Count > 1) long.TryParse(meta[1].Trim(), out unixTs);

        // OS：os-release 的 PRETTY_NAME/NAME + 可选的 lsb_release + uname -r / -m
        var osLines = sections.GetValueOrDefault("OS") ?? new List<string>();
        string? os = null, kernel = null, arch = null;
        foreach (var l in osLines)
        {
            if (os == null && l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                os = l.Substring("PRETTY_NAME=".Length).Trim('"');
            if (l.StartsWith("NAME=", StringComparison.Ordinal)) os ??= l.Substring(5).Trim('"');
        }
        if (osLines.Count >= 2)
        {
            kernel = osLines[^2]?.Trim();
            arch = osLines[^1]?.Trim();
            if (osLines.Count >= 3)
            {
                var mid = osLines[^3];
                // 中间那行若不是 key=value（即 lsb_release 的自由文本），优先用作发行版名
                if (!string.IsNullOrWhiteSpace(mid) && !mid.Contains('=')) os = mid.Trim();
            }
        }

        // 开机时间
        long uptime = 0;
        var up = sections.GetValueOrDefault("UPTIME")?.FirstOrDefault()?.Trim();
        if (up != null && double.TryParse(up.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var upSec))
            uptime = (long)upSec;

        // CPU：两行 `/proc/stat`，各字段 user nice system idle iowait irq softirq steal
        double? cpuPercent = null;
        var cpu = sections.GetValueOrDefault("CPU") ?? new List<string>();
        if (cpu.Count >= 2)
        {
            var a = TryCpu(cpu[0]); var b = TryCpu(cpu[1]);
            if (a != null && b != null && b.Value.Total > a.Value.Total)
            {
                var dTot = b.Value.Total - a.Value.Total;
                var dIdle = b.Value.Idle - a.Value.Idle;
                cpuPercent = dTot > 0 ? Math.Clamp((1 - (double)dIdle / dTot) * 100, 0, 100) : 0;
            }
        }

        // 内存 / 交换（kB）
        var mem = new Dictionary<string, long>();
        foreach (var l in sections.GetValueOrDefault("MEM") ?? new List<string>())
        {
            var p = l.Split(':');
            if (p.Length == 2 && long.TryParse(p[1].Trim().Split(' ')[0], out var v)) mem[p[0]] = v;
        }
        long memTotal = mem.GetValueOrDefault("MemTotal");
        long memAvailable = mem.GetValueOrDefault("MemAvailable");
        long memFree = mem.GetValueOrDefault("MemFree");
        long memUsed = memTotal > 0 ? Math.Max(0, memTotal - memAvailable) : 0;
        double memPercent = memTotal > 0 ? Math.Clamp((double)memUsed / memTotal * 100, 0, 100) : 0;
        long swapTotal = mem.GetValueOrDefault("SwapTotal");
        long swapUsed = swapTotal > 0 ? Math.Max(0, swapTotal - mem.GetValueOrDefault("SwapFree")) : 0;
        double swapPercent = swapTotal > 0 ? Math.Clamp((double)swapUsed / swapTotal * 100, 0, 100) : 0;

        // 磁盘
        var disks = new List<DiskStatus>();
        foreach (var l in sections.GetValueOrDefault("DISK") ?? new List<string>())
        {
            var p = l.Split('|');
            if (p.Length == 6)
                disks.Add(new DiskStatus(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim(), p[5].Trim()));
        }

        // 网络：name|state|rx|tx
        var nets = new List<NetStatus>();
        foreach (var l in sections.GetValueOrDefault("NET") ?? new List<string>())
        {
            var p = l.Split('|');
            if (p.Length == 4 &&
                long.TryParse(p[2].Trim(), out var rx) &&
                long.TryParse(p[3].Trim(), out var tx))
                nets.Add(new NetStatus(p[0].Trim(), p[1].Trim(), rx, tx));
        }

        // 若 /proc/uptime 缺失（很可能非 Linux），按 hostname 时间戳兜底估算不了，置 0 由前端提示
        return new SystemStatus(
            hostname, unixTs, os, kernel, arch, uptime,
            cpuPercent,
            memTotal, memUsed, memFree, memAvailable, Math.Round(memPercent, 1),
            swapTotal, swapUsed, Math.Round(swapPercent, 1),
            disks, nets);
    }

    private static (long Total, long Idle)? TryCpu(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8 || parts[0] != "cpu") return null;
        long tot = 0, idle = 0;
        long[] f = new long[8];
        for (int i = 1; i <= 7 && i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], out f[i])) return null;
            tot += f[i];
        }
        // idle = idle(4) + iowait(5)；guest 已计入 user，不再累加
        idle = f[4] + f[5];
        return (tot, idle);
    }

    // ---- 网络速率：跨请求对 /proc/net/dev 的累计字节做差值 / 时间差（B/s）----
    // 状态栏每 10s 刷新一次，故速率即该刷新窗口内的平均速率。
    private sealed record NetSnapshot(DateTime Ts, Dictionary<string, (long Rx, long Tx)> Ifaces);

    private static readonly ConcurrentDictionary<string, NetSnapshot> _netSnap = new();

    public static void ApplyRates(string connId, List<NetStatus> nets)
    {
        if (nets.Count == 0) return;
        var now = DateTime.UtcNow;
        var cur = nets.ToDictionary(n => n.Name, n => (n.RxBytes, n.TxBytes));
        if (_netSnap.TryGetValue(connId, out var last))
        {
            var dt = (now - last.Ts).TotalSeconds;
            if (dt >= 0.5)
            {
                for (int i = 0; i < nets.Count; i++)
                {
                    if (last.Ifaces.TryGetValue(nets[i].Name, out var p))
                    {
                        var rx = Math.Max(0, nets[i].RxBytes - p.Rx) / dt;
                        var tx = Math.Max(0, nets[i].TxBytes - p.Tx) / dt;
                        nets[i] = nets[i] with { RxRateBps = rx, TxRateBps = tx };
                    }
                }
            }
        }
        _netSnap[connId] = new NetSnapshot(now, cur);
    }

    // ---- 实时上下行（MobaXterm 风格）：一条常驻 exec 通道里循环 cat /proc/net/dev ----
    // 不用 awk / 不读 /sys（最小化容器里都可能缺），字段解析全在 C# 侧做，兼容性最好。
    public const string NetTickMarker = "===TICK===";

    public static string NetStreamScript(int intervalSec) => Sh(
        "while :; do cat /proc/net/dev 2>/dev/null || exit 1; echo \"" + NetTickMarker + "\"; " +
        "sleep " + intervalSec + "; done\n");

    /// <summary>解析 /proc/net/dev 的一次快照（`name: rx … tx …`，rx=第1列、tx=第9列，跳过 lo 和两行表头）。</summary>
    public static List<NetStatus> ParseNetDev(IEnumerable<string> block)
    {
        var list = new List<NetStatus>();
        foreach (var raw in block)
        {
            var line = raw.Trim().TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line.Substring(0, colon).Trim();
            if (name.Length == 0 || name == "lo" || name.Contains(' ') || name.Contains('|')) continue;
            var f = line.Substring(colon + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9) continue;
            if (!long.TryParse(f[0], out var rx) || !long.TryParse(f[8], out var tx)) continue;
            list.Add(new NetStatus(name, "unknown", rx, tx));
        }
        return list;
    }

    /// <summary>用相邻两次快照算每张网卡的瞬时速率（B/s）。计数器回绕/重置时按 0 处理。</summary>
    public static List<NetStatus> WithRates(List<NetStatus> cur, List<NetStatus>? prev, double dt)
    {
        if (prev is null || dt <= 0) return cur;
        for (int i = 0; i < cur.Count; i++)
        {
            var p = prev.FirstOrDefault(x => x.Name == cur[i].Name);
            if (p is null) continue;
            var rx = cur[i].RxBytes >= p.RxBytes ? (cur[i].RxBytes - p.RxBytes) / dt : 0;
            var tx = cur[i].TxBytes >= p.TxBytes ? (cur[i].TxBytes - p.TxBytes) / dt : 0;
            cur[i] = cur[i] with { RxRateBps = rx, TxRateBps = tx };
        }
        return cur;
    }
}
