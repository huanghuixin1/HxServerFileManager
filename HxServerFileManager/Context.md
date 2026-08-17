# HxServerFileManager · 上下文简报

> 更新：2026-08-14 ｜ 控制台项目（Exe）+ Kestrel + SSH.NET 管理 Linux 服务器

## 架构
- 后端：.NET 10 控制台，`FrameworkReference AspNetCore` 启用 Kestrel；SSH.NET 连接 Linux（SFTP + SSH）。监听默认 **5101**（`PORT` 可改）。
- 前端：Vue3 + Vite 工程（`client/`），构建产物输出到 `../wwwroot`。

## 已完成
- ✅ 连接列表「重连」→「连接」+ 立即开 tab：右侧已保存连接列表的按钮改为「连接」，点击走 App.vue `openSaved` 立即开占位 tab（与顶栏「已保存连接」下拉一致）。
- ✅ 删除非空文件夹修复：`/api/delete` 原来对目录走 SFTP `DeleteDirectory`（只删空目录，有内容直接抛异常），且 `sftp.DeleteFile` 对符号链接会跟随并删掉链接目标（危险）。现一律走远端 `rm -rf --`：文件/空目录/非空目录/链接通吃，链接只删链接本身；加根目录/`..` 穿越守卫。
- ✅ 命令历史 + 版本号：**历史 SSH 命令查看/双击执行** —— Terminal 实际执行过的命令（快捷命令回车 + 交互终端按回车）记入 `Data/settings.json`（`CommandHistoryItem`：connKey/command/cwd/exitStatus/createdAt，按连接隔离，同一命令重复执行只留最新一条，每连接上限 200 条），端点 `GET/POST/DELETE /api/settings/history`；前端 `useSettings.history` 全局状态 + Terminal「命令历史」按钮弹窗（时间/命令/目录/状态，**双击一行或点「执行」再次执行**——交互模式直接发到终端并回车、快捷命令模式填入输入框立即跑），可「清空本连接历史」（确认后 DELETE）。交互终端记录靠 `sendInput` 按回车切分输入缓冲（控制字符/转义序列/方向键清缓冲，避免拼出脏行）。**版本号**：两项目 csproj 加 `<Version>1.0.0</Version>`；HX 启动时打印 `版本 1.0.0`、`/api/health` 返回 `version` 字段（`WebHost.AppVersion()` 读 InformationalVersion 去 `+哈希` 后缀）；桌面壳窗口标题 `HxServerFileManager v1.0.0`（同样读 `WebHost.AppVersion()`，与 HX 一致）。
- ✅ 网络状态修复 + 实时上下行：网卡列表一直为空的根因是**源码里的 CRLF 泄漏进远端 shell**（NET 段 awk 末行 `2>&1` 变成 `2>&1\r` → bash `ambiguous redirect`，命令根本没执行），修复为发送前统一 `Sh()` 归一化换行。新增 SSE `GET /api/net-stream?connId=&interval=1`：常驻一条 SSH exec 通道每秒 `cat /proc/net/dev`，后端按相邻两帧算 B/s 推给前端；迷你状态栏显示 `↓/↑` 实时速率，详情表网卡速率/累计字节走实时流、连接状态仍来自 10s 轮询。
- ✅ 磁盘视图排除 docker/containerd 容器挂载：`/api/system-status` 的 df awk 过滤 `/var/lib/docker/`、`/var/lib/containerd/` 前缀挂载点（overlay2/<hash>、containers/<id>/shm、snapshot 等每容器一条的噪音），`/var/lib/docker` 根挂载保留；前端 SystemStatus 详情表格（`viewDisks`）与迷你条（`mainDisks`）同样过滤。
- ✅ 批量下载报错修复 + 停止下载：`/api/download`、`/api/download-many` 流式写完响应体后返回 `Results.Ok()` 会抛 “StatusCode cannot be set because the response has already started”（响应已开始不能再设状态码），Kestrel 掐断连接 → 客户端报 “Error while copying content to a stream / response ended prematurely”——已改 `Results.Empty`（与 SSE 端点同一坑）。批量下载支持「停止下载」：前端按钮 → `downloadManyCancel` 消息 → C# 按 id 取消在途请求 → 服务端 RequestAborted 终止远端 tar → 本地清理已解包的部分文件。顺带：桌面端改 `ResponseHeadersRead` 流式（原 PostAsync 会整包缓冲进内存）；tar 命令加 `--` 支持 `-` 开头文件名；错误回执带 `innerError` 显示真实根因。
- ✅ 桌面壳导出/下载选保存路径：**桌面端**弹原生「另存为」对话框（Win32 `GetSaveFileName`，消息桥 `window.external.sendMessage/receiveMessage`，C# 写文件/流式下载后回传结果）；**浏览器端**保持 `<a download>` 默认下载目录。判定：`/api/health` 返回 `desktop` 标记（桌面壳进程设 `HXSFM_DESKTOP=1`）；导出保存时自动补 `.json` 后缀。**下载**对话框预填远端文件名（用户删掉扩展名时按原扩展名自动补回）。已修两个坑：Photino `ShowSaveFile` 的 defaultPath bug（改用 Win32 对话框彻底绕开）；下载超时/闪退（关 Kestrel 响应速率限制 + 下载期间 Touch 防会话回收 + 窗口关闭时取消在途下载并禁止再回传消息）。**批量下载**（操作菜单多选文件/文件夹）：桌面端弹文件夹选择器（`SHBrowseForFolder`）选一个本地目录，远端 `tar` 流打包 → C# `TarReader` 解包到该目录保留目录结构；浏览器端逐个 `<a download>` 下载文件（目录跳过）。后端 `/api/download-many` 用 `BeginExecute`+`OutputStream` 流式直出，busybox tar 兼容。
- ✅ 交互终端剪贴板：**选中即复制**（xterm `onSelectionChange` → Clipboard API，兜底隐藏 textarea + execCommand）+ **右键粘贴**（`rightClickSelectsWord: false`，接管 contextmenu 读剪贴板）；粘贴内容以回车结尾时弹窗询问是否执行，内容可编辑（执行 / 仅粘贴 / 取消，Ctrl+Enter 快捷执行）。
- ✅ 后端三增强：连接信息持久化到 `Data/connections.json`、文本文件 `GET/PUT /api/file-content` 在线编辑、`GET /api/logs/stream` SSE 实时日志。编译+启动已验证。
- ✅ 双击打开提速：`GET /api/file-content` 改为**原始字节流返回**（不再 JSON 包裹，避免 System.Text.Json 对非 ASCII 的 `\uXXXX` 转义膨胀 + 浏览器 JSON.parse 开销），二进制检查改为开头 64KB NUL 嗅探，前端 fetch 流式读取边收边显示（编辑器带进度条，接近终端 cat 的渐进体验）。
- ✅ 上传文件夹 + 拖拽上传：文件列表支持**拖拽上传**（文件/文件夹/混搭一次拖入，浏览器唯一能同时选两者的方式；`webkitGetAsEntry` 递归遍历保留目录结构与空目录），「操作」下拉只留一个「上传」（多选文件）；后端 `EnsureRemoteDir` 递归建目录 + `POST /api/ensure-dirs` 批量创建（已存在跳过），再逐文件上传保留相对路径。
- ✅ 服务器间直传（发送到连接）：两个已打开的连接之间直接传文件/文件夹，**不经本机中转** —— 源机执行 `scp` 直连目标机，数据在两端服务器之间流动，本机只下发指令 + 轮询状态。`POST /api/server-copy`（含目标目录自动补建、源≠目标校验）+ `GET /api/server-copy/{jobId}` 轮询；目标认证：密码走 sshpass（源机未装则尝试免密并给安装提示）、私钥走 /tmp 临时文件 + `scp -i`（带口令私钥不支持）。前端：FileManager「操作 → 发送到连接…」，App 弹窗选目标连接（去重、排除自身、默认目标目录 = 源选中项所在目录）+ 进度轮询 + 完成后自动刷新目标连接的文件列表。
- ✅ 前端已全部改为 Element Plus：`main.js` 注册 EP + 全部图标；6 组件 + `App.vue` 使用 `el-form/el-table/el-dialog/el-breadcrumb/el-button/el-tag` 等。
- ✅ `package.json` 已声明 `element-plus ^2.14.4` + `@element-plus/icons-vue ^2.3.2`。
- ✅ 后端已加 `MapFallbackToFile("index.html")`。
- ✅ `npm run build` 已覆盖 `wwwroot`（index.html + assets/，旧 app.js/style.css 已被清空）。
- ✅ **多服务器并发连接**：`App.vue` 会话标签栏（自定义 pill tab，可关闭+确认）、新建连接对话框、每标签独立 FileManager/Terminal/编辑器（`v-show` 保留各自终端历史/目录状态）。后端本就支持多会话，无需改动。
- ✅ **交互终端**：`Terminal.vue` 双模式（快捷命令 exec / 交互终端 interactive）。交互终端 = 后端 SSH.NET `ShellStream`（pty xterm-256color）+ 前端 `@xterm/xterm` 6.0.0。输出 SSE（`/api/terminal/stream`，先回放最近输出再实时推送，ShellOutput 有界 Channel 防积压），输入 `POST /api/terminal/input`，关闭 `POST /api/terminal/close`（DisposeShell）。可跑 nano/vim/read 脚本等需要 TTY 的程序。

## 文件列表空白 bug 已修（字段大小写）
- 后端 `/api/files` 的 `FileEntry` 经最小 API 序列化为 camelCase（`name/fullPath/isDirectory/size/lastWriteTimeUtc/isText`），前端 FileManager 原来读 PascalCase，导致名称/大小/修改时间全空白。已统一改读 camelCase。

## 终端默认交互模式 + 路径双向同步
- 终端模式 radio 默认选中「交互终端」（Terminal.vue `mode` 默认值 + onMounted 自动打开）；快捷命令保留为二线工具。
- 后端 `EnsureShell` 创建 shell 后注入：`export PROMPT_COMMAND='printf "\033]7;file://%s%s\007" "$HOSTNAME" "$PWD"'` + 自定义 `PS1='\u@\h:\w$ '`。bash 每次提示符前输出 OSC 7 序列（含当前目录），前端 Terminal.vue 在 SSE 流里用正则提取（跨 chunk 缓冲，剥掉序列再渲染）。
- 正向：终端 `cd /etc` → OSC 7 推送 → 文件列表面包屑自动变 `/etc`。
- 反向：文件列表导航（点目录/面包屑/上级）→ App 调 `termRefs[connId].injectCd(path)` → 向交互终端写 `cd <path>\r`，终端提示符同步变化。
- checkbox 已改名「同步路径」：**开 = 双向同步（终端 cd ⇄ 文件列表导航互相跟随）；关 = 两边完全独立**（终端 cd 不动文件列表，文件列表导航也不注入终端 cd、不更新会话 cwd）。实测：关闭时点文件列表目录终端提示符不变，打开后点「上级」终端收到 `cd /config`。
- 注意：OSC 7 依赖 bash（非 bash 默认 shell 时仅失去路径同步）；全屏程序（nano）运行时反向注入的 `cd` 会被程序接收（预期行为）。
- 真实测试机实测通过：`cd /etc` → 面包屑 `/etc`；点「上级」→ 终端 `/`；checkbox 关闭 → 文件列表不跟随。

## 终端路径持久化 + 文件列表联动
- 根因：SSH.NET 每次 `CreateCommand` 都是新 exec 通道，`cd` 不保留，所以终端总回默认目录。
- 方案：`SshSession.Cwd` 会话级 cwd（连接时初始化为 SFTP 工作目录）；`/api/command` 命令包装为 `cd <cwd> && <cmd>; rc=$?; pwd; exit $rc`，解析末尾 pwd 行更新并返回 cwd；新增 `/api/cwd` 供文件列表导航同步会话目录。
- 前端：App.vue `cwdMap` 每连接共享；FileManager 工具栏「跟随终端路径」checkbox（默认开）控制文件列表随终端 cd 跳转；双向联动（点文件列表目录也会更新终端提示符和会话 cwd）。
- 面包屑：弃用 el-breadcrumb（分隔符自带 margin 造成 `/` 右侧间隙），改自绘 span，无间隙无双斜杠。
- 顺带修复：连接后初始目录没用家目录（ConnectPanel 未把 `homeDirectory` 传给 App）。

## 已保存连接增强
- 顶栏「已保存连接」下拉：连接中也能一键打开任意已保存连接（新开标签），含「管理已保存的连接…」入口。
- 管理对话框：重连/编辑/删除。
- 编辑对话框：`PUT /api/connections/{id}`，可改别名/主机/端口/用户名/凭据；**留空字段保持不变**（凭据不随列表返回，编辑时留空即不改）。
- 别名：连接表单与编辑表单均可设置 `name`；连接/重连响应新增 `name`；标签和已保存列表显示别名（列表里别名≠主机时附带主机 tag）。
- 已用 mock 实测：连接中下拉开第二个连接、编辑改别名、下拉/管理面板同步刷新。

## 登录鉴权（HxSimpleWebAuth）
- 后端引用 `libs/HxSimpleWebAuth.dll`（net8，net10 可引用）：`WebAdminAuth` 负责凭据/token/失败锁定/IP 绑定校验。密码来源优先级：**① 环境变量 `HXSFM_WEB_PASSWORD`（可覆盖）→ ② `configs/env.json` 的 `authPwd` 字段**（`LoadConfigPassword()` 读取，文件不存在/解析失败返回 null）——设置后所有 /api（除 /api/session、/api/auth/*）必须带 Bearer token；未设置则仅本机回环可访问。`configs/env.json` 已 gitignore（存密码不入库），模板 `configs/env.json.example` 可提交。
- 端点：`GET /api/session`（required/authenticated 探测）、`POST /api/auth/login`（body `{"key":密码}` → `{token}`）、`POST /api/auth/logout`（吊销 token）。
- SSE（/api/logs/stream、/api/terminal/stream）与 `<a download>` 带不了请求头：前端把 token 放 `?token=` 查询参数，后端中间件统一转成 Authorization 头再校验。
- 前端：登录页 LoginView.vue（密码+记住我+剩余次数提示）；api.js 统一带 Bearer 头、401 触发回登录页；token 存 sessionStorage/localStorage（hxsfm_auth_token）；认证通过后才恢复会话/路径；退出登录吊销 token + 断开所有 SSH 会话。
- 实测（curl + 浏览器）：无 token 401、错密码提示剩余次数、正确登录拿 token、SSE/download 带 ?token= 通过、登出后 token 失效、刷新记住登录、退出回登录页全部通过。
- 上传上限：`configs/env.json` 的 `maxUploadMb`（默认 1GB，`0`=不限制），环境变量 `HXSFM_MAX_UPLOAD_MB` 可覆盖（优先）；桌面壳（Desktop）启动时强制设为 0，忽略大小限制。`/api/health` 返回 `maxUploadBytes`（0=不限制），前端据此预校验。

## 文件列表：操作下拉 + 行多选
- 「上传/新建目录/删除」收进「操作」下拉（el-dropdown：新建目录/上传/删除，删除为**批量删除**——未选中时禁用、显示「删除（N）」数量，确认后逐个删除并刷新），工具栏只剩 同步路径/上级/刷新/操作。
- 行选择改为 Windows/Mac 风格：隐藏 selection 列 checkbox（CSS display:none，列宽 1px）；`@row-click` 自处理——普通单击单选、Ctrl/Cmd 加减选、Shift 范围选（lastSelected 到当前行），`row-key=fullPath`，`.row-selected` 高亮。双击目录/文件仍打开/编辑。
- 实测：单选、Ctrl 加选、Meta 减选、Shift 范围选（logs→test 中间 4 项全选）、批量删除（btest 内 4 项、3 个 txt 各验一次）、新建目录对话框/上传触发均正常。

## 交互终端实测（真实测试机 192.168.31.254:2222）
- `ls -la` 输出、`read -p` 等待输入并回显、nano 全屏打开并输入文字（标题显示 Modified）均正常；切回快捷命令模式 shell 正确关闭。
- 修复：SSE 客户端断开后 `Results.Ok()` 重复设状态码抛异常 → 改 `Results.Empty`，日志已无异常。
- 注意：Ctrl 组合键（Ctrl+X 等）无法用合成事件自动化测试，真实键盘可用（xterm 标准处理）。
- 部署提醒：交互终端依赖 `@xterm/xterm`，`npm run build` 会打进产物；bin 里运行需重新 `dotnet build` 同步 wwwroot。

## 字段名 bug 已全部修复（connId → connectionId）
- 断开：`api.disconnect` 改发 JSON `{ connectionId }`，后端 `IdRequest` 可正确绑定。
- 重连：`api.reconnect` 改发 JSON `{ connectionId }`，不再 404。
- 补修：`mkdir / rename / delete / command / saveFileContent` 之前同样发 `connId` 而绑定失败，已全部改为 `connectionId`（rename 的 `dir/oldName` 本就是 `path/name`，无需改）。**教训：后端 POST 一律读 `ConnectionId`，前端发 `connId` 会静默失败。**
- dev proxy：`vite.config.js` 指向 `http://localhost:5101`。

## 验证情况
- ✅ 后端编译 0 错、启动、`/api/health`、首页与静态资源、SPA fallback、断开绑定、连接失败错误提示、日志 SSE 均已在本机验证。
- ✅ 多连接 UI 已用临时 mock 后端（node，纯内存）在浏览器实测：连两台不同主机、标签切换、每标签终端历史/文件列表独立、关闭标签确认+断开+自动切到剩余标签、编辑器按标签绑定保存。
- ⏳ 真机端到端（连 Docker 测试机）尚未跑：本环境无 docker，需在装有 Docker 的机器上执行。

## 测试机（test-linux/）
`docker compose up -d --build` → 容器 22 映射本机 2222，`testuser/testpass`。

## 安全提醒
- `connections.json` 明文存密码/私钥，仅限本地/内网。
- 访问控制：设密码（`HXSFM_WEB_PASSWORD` 或 `configs/env.json` 的 `authPwd`）后所有接口需登录；未设置时仅本机回环可访问。局域网/公网使用必须设置强密码。

## 运行
```bash
# 带登录鉴权（密码写进 configs/env.json 的 authPwd，模板见 configs/env.json.example）
PORT=5101 ./bin/Debug/net10.0/HxServerFileManager.exe
# 或用环境变量覆盖：HXSFM_WEB_PASSWORD=你的密码 PORT=5101 ...
cd client && npm run build      # 前端产物 -> ../wwwroot
```
