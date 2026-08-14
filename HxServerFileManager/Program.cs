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
using System.Text;
using System.Text.Json;
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
// 注意：connections.json 中以明文保存凭据，仅供本地/内网测试使用，
//       请勿在公网环境直接暴露本服务。
// ----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// 显式配置 Kestrel（端口可由环境变量 PORT 覆盖，默认 5101）
var listenPort = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5101;
builder.WebHost.UseKestrel(kestrel =>
{
    kestrel.ListenAnyIP(listenPort);
    // 允许较大文件上传（默认 200MB）
    kestrel.Limits.MaxRequestBodySize = 200 * 1024 * 1024;
});

// 单例：会话表 / 操作日志 / 连接存储
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<OperationLogger>();
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "Data");
builder.Services.AddSingleton(new ConnectionsStore(dataDir));

var app = builder.Build();

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
app.MapGet("/api/download", async (string connId, string path, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(connId);
        var stream = s.Sftp.OpenRead(path);
        var name = Path.GetFileName(path);
        log.Log("info", connId, "下载", path);
        return Results.File(stream, "application/octet-stream", name);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 读取文本文件内容（用于在线编辑；二进制/超大文件会被拒绝）
app.MapGet("/api/file-content", async (string connId, string path, ConnectionManager mgr, OperationLogger log) =>
{
    try
    {
        var s = mgr.Get(connId);
        var attr = s.Sftp.GetAttributes(path);
        if (attr.IsDirectory) return Results.BadRequest(new { error = "目标是目录，不是文件" });
        if (attr.Size > FileHelpers.MaxEditBytes) return Results.BadRequest(new { error = "文件过大，暂不支持在线编辑（>10MB）" });

        using var stream = s.Sftp.OpenRead(path);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
            return Results.BadRequest(new { error = "该文件疑似二进制，无法在浏览器中编辑" });

        var content = Encoding.UTF8.GetString(bytes);
        log.Log("info", connId, "读取文件", path);
        return Results.Ok(new { path, content, size = bytes.Length, encoding = "utf-8" });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
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

    string tail;
    lock (s.ShellTail) tail = s.ShellTail.ToString();
    if (tail.Length > 0)
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type = "out", data = tail })}\n\n", ctx.RequestAborted);
    await ctx.Response.Body.FlushAsync();

    try
    {
        await foreach (var chunk in ch.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            var data = Encoding.UTF8.GetString(chunk);
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type = "out", data })}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { /* 客户端断开 */ }
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

    // 输出转发任务：shell 输出 channel → WebSocket
    var pump = Task.Run(async () =>
    {
        try
        {
            // 先回放 tail，再实时推送（与 SSE 行为一致）
            string tail;
            lock (s.ShellTail) tail = s.ShellTail.ToString();
            if (tail.Length > 0)
                await SendWsJsonAsync(ws, new { type = "out", data = tail }, ctx.RequestAborted);

            await foreach (var chunk in ch.Reader.ReadAllAsync(ctx.RequestAborted))
            {
                var data = Encoding.UTF8.GetString(chunk);
                await SendWsJsonAsync(ws, new { type = "out", data }, ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) { /* 客户端断开 */ }
        catch (Exception) { /* channel 关闭等 */ }
    }, ctx.RequestAborted);

    // 主循环：读取入站 WebSocket 帧 → shell stdin
    try
    {
        var buf = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(buf, ctx.RequestAborted);
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
                                 && doc.RootElement.TryGetProperty("rows", out var rowsEl))
                        {
                            // ShellStream 不支持动态 resize，忽略（与现有行为一致）
                        }
                    }
                }
            }
            catch (JsonException) { /* 非法 JSON，忽略 */ }
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }

    // 等输出转发结束（取消 token 已触发，pump 会快速退出）
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

    // 先回放最近若干条，避免新客户端看不到历史
    foreach (var e in log.Recent)
        await WriteLogEvent(ctx, e);
    await ctx.Response.Body.FlushAsync();

    try
    {
        await foreach (var e in log.Stream.WithCancellation(ctx.RequestAborted))
        {
            await WriteLogEvent(ctx, e);
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { /* 客户端断开 */ }
});

// 健康检查
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// ---- 空闲会话回收 ----
var mgr = app.Services.GetRequiredService<ConnectionManager>();
var cts = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());
_ = Task.Run(() => mgr.CleanupLoop(cts.Token));

Console.WriteLine($"[HxServerFileManager] Kestrel 已启动，监听 http://0.0.0.0:{listenPort}");
app.Run();

// ----------------------------------------------------------------------------
// 辅助函数 / 类型
// ----------------------------------------------------------------------------

static string CombinePath(string dir, string name)
{
    dir = (dir ?? "/").TrimEnd('/');
    if (dir == "") dir = "";
    return dir + "/" + name.TrimStart('/');
}

// 单引号转义，用于安全地把路径拼进 sh 命令`
static string Shq(string s) => "'" + s.Replace("'", "'\\''") + "'";

// ----------------------------------------------------------------------------
// 鉴权辅助（HxSimpleWebAuth）
// ----------------------------------------------------------------------------

/// <summary>
/// 读取 configs/env.json 中的 authPwd（模板见 configs/env.json.example）。
/// 文件不存在或解析失败返回 null（此时回退到环境变量/未配置）。
/// </summary>
static string? LoadConfigPassword(string contentRoot)
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

static bool IsLoopbackAddress(System.Net.IPAddress? address)
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
static HttpRequestData CreateAuthRequest(HttpContext context, string body = "", string? method = null)
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
    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return new HttpRequestData(
        (method ?? context.Request.Method).ToUpperInvariant(),
        target,
        headers,
        body,
        remoteIp);
}

static async Task<HttpRequestData> CreateAuthRequestAsync(HttpContext context)
{
    context.Request.EnableBuffering();
    using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
    var body = await reader.ReadToEndAsync(context.RequestAborted);
    context.Request.Body.Position = 0;
    return CreateAuthRequest(context, body);
}

static async Task WriteAuthResponseAsync(HttpContext context, ApiResponse response)
{
    context.Response.StatusCode = response.StatusCode;
    if (response.AllowHeader is not null)
        context.Response.Headers.Allow = response.AllowHeader;
    context.Response.ContentType = "application/json; charset=utf-8";
    context.Response.ContentLength = response.Body.Length;
    await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
}

static async Task SendWsJsonAsync(WebSocket ws, object payload, CancellationToken ct)
{
    var json = JsonSerializer.Serialize(payload);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
}

// 真正执行连接（纯逻辑，不含持久化/日志，便于 connect 与 reconnect 复用）
static (bool ok, string? connectionId, string? home, string? error) ConnectInternal(ConnectRequest req, ConnectionManager mgr)
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
        ssh.Connect();
        sftp.Connect();

        string home = "/";
        try { home = sftp.WorkingDirectory; } catch { /* ignore */ }

        var id = mgr.Add(new SshSession(ssh, sftp, home));
        return (true, id, home, null);
    }
    catch (Exception ex)
    {
        return (false, null, null, "连接失败: " + ex.Message);
    }
}

static ConnectionProfile ToProfile(ConnectRequest req, int port) => new(
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

static async Task WriteLogEvent(HttpContext ctx, LogEntry e)
{
    var json = JsonSerializer.Serialize(e);
    await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
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
public record RenameRequest(string ConnectionId, string Path, string Name, string NewPath);
public record CommandRequest(string ConnectionId, string Command);
public record FileContentRequest(string ConnectionId, string Path, string Content);
public record CwdRequest(string ConnectionId, string Path);
public record TerminalOpenRequest(string ConnectionId, int? Cols, int? Rows);
public record TerminalInputRequest(string ConnectionId, string Data);

public record FileEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime LastWriteTimeUtc, bool IsText);

public record LogEntry(DateTime Time, string Level, string Connection, string Action, string Detail, string? Result);

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
/// 已保存的连接配置（持久化到 Data/connections.json，凭据明文存储，仅供本地/内网）。
/// </summary>
public sealed class ConnectionsStore
{
    private readonly string _file;
    private readonly object _gate = new();
    private List<ConnectionProfile> _profiles;

    public ConnectionsStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "connections.json");
        _profiles = Load();
    }

    private List<ConnectionProfile> Load()
    {
        if (!File.Exists(_file)) return new();
        try { return JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(_file)) ?? new(); }
        catch { return new(); }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_file, json);
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
