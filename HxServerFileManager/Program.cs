using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Collections.Concurrent;
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

    // 连接成功后持久化（相同 host:port:username 会更新而非新增）
    store.Upsert(ToProfile(req, port));
    log.Log("info", $"{req.Username}@{req.Host}:{port}", "连接", "建立 SSH/SFTP 会话", "成功");
    return Results.Ok(new
    {
        connectionId = id,
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

// 单引号转义，用于安全地把路径拼进 sh 命令
static string Shq(string s) => "'" + s.Replace("'", "'\\''") + "'";

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

    public SshSession(SshClient ssh, SftpClient sftp, string cwd)
    {
        Ssh = ssh;
        Sftp = sftp;
        Cwd = cwd;
    }

    public void Touch() => LastUsedUtc = DateTime.UtcNow;

    public void Dispose()
    {
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

    // 以 host|port|username 去重；已存在则保留原 Id/CreatedAt 并更新凭据与时间
    public void Upsert(ConnectionProfile p)
    {
        lock (_gate)
        {
            var key = $"{p.Host}|{p.Port}|{p.Username}";
            var existing = _profiles.FirstOrDefault(x => $"{x.Host}|{x.Port}|{x.Username}" == key);
            if (existing is null)
            {
                _profiles.Add(p);
            }
            else
            {
                _profiles.Remove(existing);
                _profiles.Add(p with { Id = existing.Id, CreatedAt = existing.CreatedAt });
            }
            Save();
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
