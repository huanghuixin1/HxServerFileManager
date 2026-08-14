# Project Memory

以后处理这个项目时，先读这个文件。

## 项目信息
- 本项目是.net10实现的webssh服务
- 结构：`Program.cs`（Kestrel 后端，SSH.NET 管理 Linux）+ `client/`（Vue3 + Vite + Element Plus 前端，构建产物输出到 `wwwroot/`）
- 后端默认监听 **5101**（`PORT` 环境变量可覆盖；本机 shell 里 `PORT=0` 时会随机端口，属环境特性不是 bug）
- **运行方式（重要）**：用户直接运行 `bin/Debug/net10.0/HxServerFileManager.exe`，ContentRootPath=启动时 cwd，所以 **wwwroot 页面和 Data/connections.json 都从 exe 所在/启动目录读取**（bin 里的那一份）。改前端后必须 `npm run build` 且 **`dotnet build` 把 wwwroot 同步进 bin**，否则用户看到的还是旧页面。本地测试：`cd bin/Debug/net10.0 && PORT=5101 ./HxServerFileManager.exe`
- `test-linux/` 提供 Docker 测试机；真实测试机：`192.168.31.254:2222/2223`，`testuser/testpass`（用户自建）

## 长期规则
- 支持PC和手机端浏览（响应式布局，media query 单列切换）

## 重要文件
- `Program.cs`：全部后端逻辑（最小 API + 单文件类型定义）。**JSON body 绑定一律读 `ConnectionId`**（camelCase `connectionId`），前端 `api.js` 必须发 `connectionId` 字段，发 `connId` 会静默绑定为 null（历史教训，见已知问题）
- `client/src/App.vue`：多连接标签页架构。`connections[]` 存所有活跃会话，`activeId` 当前标签；标签可关闭（确认后断开）；工作区用 `v-show` 按连接渲染，保证每会话的终端历史/文件浏览器状态独立
- `client/src/api.js`：所有 /api 封装。POST 类接口统一发 `{ connectionId, ... }`；文件接口参数名与后端 record 一一对应（如 rename 用 `path`+`name`，不是 `dir`/`oldName`）
- `client/src/components/`：ConnectPanel（连接表单，可内联也可在对话框里复用）、SavedConnections、FileManager（el-table + 自绘面包屑）、Terminal（双模式：快捷命令 exec + 交互终端 xterm）、LogPanel（SSE）、EditorModal
- 终端双模式：**快捷命令（exec）** 一次一命令带 cwd 持久化；**交互终端（interactive）** 用 SSH.NET `ShellStream`（pty，xterm-256color）+ 前端 `@xterm/xterm`，可跑 nano/vim/read 脚本等需要 TTY/stdin 的程序。输出走 SSE（`/api/terminal/stream`，先回放 ShellTail 再实时推送），输入走 `POST /api/terminal/input`，每会话惰性创建/关闭（`SshSession.EnsureShell/DisposeShell`），ShellOutput 为有界 Channel（DropWrite）防无消费者时积压，ShellTail 供新 SSE 消费者回放。切回 exec 模式会 DisposeShell（nano 等随之终止）
- 会话工作目录联动：`SshSession.Cwd` 为会话级 cwd（连接时初始化为 SFTP 工作目录）。`/api/command` 把命令包装为 `cd <cwd> && <cmd>; rc=$?; pwd; exit $rc`，解析末尾 pwd 行更新 cwd 并返回；`/api/cwd` 供文件列表导航时同步会话 cwd。前端 App.vue 持 `cwdMap`（每连接共享），FileManager「同步路径」checkbox（默认开）控制双向联动——开：终端 cd ⇄ 文件列表导航互相跟随；关：两边完全独立（终端 cd 不动文件列表，文件列表导航也不注入终端 cd、不同步 cwd）
- **交互终端路径同步（OSC 7）**：后端 `EnsureShell` 注入 `PROMPT_COMMAND`（printf OSC 7 序列 `\033]7;file://$HOSTNAME$PWD\007`）+ 自定义 PS1。bash 每次打印提示符前输出 OSC 7 携带当前目录；前端 Terminal.vue 在 SSE 数据里正则提取（跨 chunk 缓冲，`extractOsc7`），剥掉序列再 `xterm.write`，解析出的 path 通过 `update:cwd` 推给 App 同步文件列表。反向：App.vue 的 `onNavigate`（文件列表导航）调用 `termRefs[connId].injectCd(path)` 向交互终端写 `cd <path>\r`（Terminal 内判断是否交互模式；全屏程序运行时会被吞进程序，属预期）。**「同步路径」关闭时 `onNavigate`/`onCwdChanged` 直接 return，两端互不干扰**。OSC 7 依赖 bash（非 bash 时仅失去路径同步）
- `client/vite.config.js`：dev proxy 指向 `http://localhost:5101`

## 登录鉴权（HxSimpleWebAuth）
- 鉴权库：`libs/HxSimpleWebAuth.dll`（从 BackDatabase 项目拷来，net8 程序集 net10 可引用；csproj `<Reference HintPath=libs\...>` + `<None Include=libs\**\* CopyToOutputDirectory>`，构建会拷到 bin）。API：`new WebAdminAuth(password, logDirectory, envVarName)`；`Authorize(HttpRequestData)` 校验 Bearer token；`Handle(HttpRequestData, path)` 处理登录/登出（登录 POST body 用 `{"key": "<密码>"}`，返回 `{token, expiresAt}`，错误时 `{error, locked, remainingAttempts}`）；`IsAuthPath(path)` 判定 `/api/auth/login|logout`；`HttpRequestData(Method, Target, Headers, Body, RemoteIp)`；`ApiResponse(StatusCode, Body, AllowHeader)` + `Json/Error` 静态方法。**token 只认 Authorization: Bearer 头，不认查询参数**
- 密码配置：**优先级 ① 环境变量 `HXSFM_WEB_PASSWORD`（可覆盖）→ ② `configs/env.json` 的 `authPwd` 字段（模板 `configs/env.json.example`，`LoadConfigPassword()` 读取）**。`configs/env.json` 已加入根 .gitignore（存密码不入库），`env.json.example` 可提交。**配置了密码**：所有 /api（除 /api/session 与 /api/auth/*）必须带有效 token；**未配置**：仅本机回环可访问（fail-closed，防止内网裸奔；手机/局域网访问必须设密码）
- 中间件位于 `app.Use(...)`（静态文件之前），`CreateAuthRequest(Async)` 把 HttpContext 转 HttpRequestData；**EventSource/<a download> 带不了请求头**，前端把 token 放 `?token=` 查询参数，中间件检测到无 Authorization 头时自动补 `Authorization: Bearer <token>` 再校验（实测 SSE/download 均通过）
- 端点：`GET /api/session`（返回 `{required, authenticated}`，前端据此显示登录页）、`POST /api/auth/login`、`POST /api/auth/logout`（吊销 token，之后原 token 变 401）
- 前端（api.js）：token 存 sessionStorage + 勾选记住时存 localStorage（`hxsfm_auth_token`）；`request()` 统一带 Bearer 头，401（非登录接口）触发 `setUnauthorizedHandler` → App 清状态回登录页；`api.login` 单独处理 401 拿 locked/remainingAttempts 展示；下载 URL 与 SSE URL 追加 `?token=`。App.vue：`checkSession` 探测后决定渲染 LoginView 还是主界面，**认证通过后才 loadSaved + restoreWorkspace**；退出登录会吊销 token + 逐个断开 SSH 会话 + 清 localStorage；登录成功（onAuthed）后同样恢复会话/路径

## 已知问题
- **Desktop 桌面壳白屏的坑（勿回退）**：① Photino.NET 必须 ≥ **4.0.16**（3.0.14 在 Windows 上建出的窗口坏掉，只剩标题栏大小）；② Photino 窗口必须在 **STA 线程** 创建——.NET 主线程默认 MTA，WebView2 在 MTA 线程初始化直接报 0x80010106（RPC_E_CHANGED_MODE）且被 Photino 静默吞掉，表现就是窗口全白、进程下没有任何 msedgewebview2 子进程；③ 入口保持同步（top-level 不能有 await）。排查手法：看进程有无 webview2 子进程 + Kestrel 日志有没有 WebView2 发出的 `GET /`（之前两次"修复"后实际没有，窗口根本没导航）
- 曾批量出现“字段名不匹配”：前端发 `connId`，后端绑 `ConnectionId`，导致 disconnect/reconnect/mkdir/rename/delete/command/save 全部静默失败或 404。已全部统一为 `connectionId`。**新增后端接口时务必核对 body 字段名**
- 后端最小 API 返回体一律 camelCase（`FileEntry` 序列化为 `name/fullPath/isDirectory/size/lastWriteTimeUtc/isText`），前端 FileManager 曾误读 PascalCase 导致列表空白，已改 camelCase。**前端读响应字段时一律用小写开头**
- 后端会话空闲 30 分钟自动回收（ConnectionManager.CleanupLoop）；前端标签不会自动消失，此时操作会报“连接不存在或已断开”，需手动断开重连
- SSE 端点（`/api/logs/stream`、`/api/terminal/stream`）在客户端断开后**不要返回 `Results.Ok()`**——响应已开始会抛 "StatusCode cannot be set because the response has already started"，用 `Results.Empty`（不触碰状态码）
- Docker 端到端（连 test-linux）尚未跑：开发环境无 docker
- `connections.json` 明文存密码/私钥，仅限本地/内网

## 进度记录
- 2026-08-14：前端整体切到 Element Plus（main.js 注册 EP+图标；组件全用 el-*）
- 2026-08-14：修复 5 处字段名 bug（connId→connectionId）、断开/重连、dev proxy 端口
- 2026-08-14：支持多服务器并发连接 —— App.vue 会话标签栏（自定义 pill tab，可关闭+确认）、新建连接对话框、每标签独立 FileManager/Terminal/EditorModal（v-show 保留状态）；后端本就支持多会话（ConcurrentDictionary），无需改动
- 2026-08-14：已保存连接增强 —— 顶栏「已保存连接」下拉（连接中也可一键再开一个，走 /api/connections/reconnect）；管理对话框；编辑对话框（`PUT /api/connections/{id}`，留空字段保持不变，凭据不返回）；连接表单/编辑表单支持别名（name 字段），标签与列表显示别名
- 2026-08-14：文件列表空白 bug —— 后端最小 API 序列化 FileEntry 为 camelCase，前端曾读 PascalCase，已统一改 camelCase
- 2026-08-14：终端 cwd 持久化 + 文件列表联动 —— SSH.NET 每次 CreateCommand 开新通道导致 cd 不保留，改为会话级 Cwd + 命令包装（pwd 兜底解析）；新增 `/api/cwd`；FileManager 加「跟随终端路径」checkbox（默认开）双向联动；自绘面包屑去掉 el-breadcrumb 的间隙/双斜杠问题；顺带修复连接后初始目录未用家目录（ConnectPanel 缺发 homeDirectory）
- 2026-08-14：终端回车丢焦点 —— el-input 在 busy 时被 `:disabled` 禁用，焦点被浏览器夺走；改为执行期间不禁用输入框 + `restoreFocus()`（ref.focus）兜底，真实测试机实测连续命令焦点保持
- 2026-08-14：交互终端 —— 后端 `ShellStream` + pty（`/api/terminal/open|stream|input|close`）+ 前端 xterm.js（`@xterm/xterm` 6.0.0，Terminal.vue 双模式 radio）。实测连真实测试机：ls、read 交互输入回显、nano 全屏打开并输入文字均正常；修掉 SSE 关闭时 `Results.Ok()` 重复设状态码异常；Ctrl 组合键无法用合成事件自动化测试（工具限制），真实键盘可用
- 2026-08-14：终端默认交互模式 + 路径双向同步 —— 默认选中「交互终端」（onMounted 自动打开）；后端注入 PROMPT_COMMAND 让 bash 提示符输出 OSC 7 携带 cwd，前端解析后文件列表跟随（终端 `cd /etc` → 面包屑变 `/etc`）；文件列表导航向终端注入 `cd`（点「上级」→ 终端提示符变 `/`）。checkbox（同步路径）关闭后两边完全独立：终端 cd 不动文件列表，文件列表导航也不注入终端 cd（实测：关闭时点 logs 目录终端提示符不变；打开后点上级终端收到 `cd /config`）。真实测试机实测通过
- 2026-08-14：工作区高度修复 —— `.workspace` grid 只有 columns 没有 rows，子项 `height:100%` 参照不到确定行高回退成内容高度，导致 FileManager/Terminal 卡片矮半截（.term 曾只有 119px、xterm 显示区 37px）。修复：`grid-template-rows: minmax(0,1fr)`（窄屏 media query 改 `1fr 1fr` 两行均分）；xterm 补 `.xterm-viewport height:100%`；`openInteractive` 加 `waitForSize()` 等容器有实际尺寸再算 pty 行列数。双列实测 .fm/.term 均 256px 撑满。
- 2026-08-14：会话本地化持久化 —— 打开中的 SSH 会话 + 各自 cwd 存 localStorage（`hx_workspace_v1`，防抖 400ms 写入），刷新/下次打开自动恢复：`restoreWorkspace` onMounted 逐个 `reconnect(profileId)` + 恢复路径。后端 connect/reconnect 响应新增 `profileId`（Upsert 返回最终 profile，去重后保留原 Id），前端 SavedConnections/App 透传。两个坑：① FileManager externalPath watch 必须 `immediate: true`（挂载时已有恢复路径才触发）；② 交互终端 shell 启动时 OSC 7 会先推初始目录覆盖恢复路径——Terminal 注入恢复 cd 期间 `oscIgnored` 忽略 OSC 7（500ms），恢复完成后才推给 App。断开连接不清 localStorage（下次仍自动恢复）；删除 saved connection 后 profileId 失效自动跳过
- 2026-08-14：工作区布局改造 —— 终端在**左**、文件列表在**右**（workspace 改 flex，终端 `flex: 0 0 var(--term-w, 58%)`，文件 `flex: 1`）；中间 `.ws-divider` 可拖拽调宽（mousedown/mousemove，30%-75% clamp，`--term-w` CSS 变量）；终端头部最大化按钮（`termMax` → workspace.term-max，文件列表/分隔条隐藏，终端占满，再次点击还原）；窄屏单列终端在上文件在下，**分隔条横放可拖拽调高度**（`startResize` 按 `getComputedStyle(el).flexDirection` 自动判断拖宽度还是高度，`--term-h` 变量，30%-75% clamp；双列拖 `--term-w`）；文件列表修改时间格式去掉秒；实时操作日志默认隐藏（`logEnabled` 默认 false，顶栏按钮可开关）；文件列表去掉操作列，改为**行右键菜单**（el-table `@row-contextmenu` → 自定义 fixed 定位菜单：编辑（isText）/下载/重命名/删除，点击外部关闭，位置 clamp 防溢出），「编辑」项不依赖 isText：非目录均显示（后端 /api/file-content 自己拒绝目录/超大/NUL 二进制，前端只管放行）（`YYYY/MM/DD HH:mm`）。**xterm 6 高度链**：`.xterm`（height:100%）→ `.xterm-viewport`（absolute+100%）→ `.xterm-scrollable-element`（**必须 height:100%**，xterm.css 未给它高度，否则保持内容高度溢出、滚动失效）→ `.xterm-screen`（100%）；`.xterm-rows`/screen 内容行由 xterm 内部 buffer 管理，scrollback 滚动靠 xterm setScrollPosition 重渲染，不依赖 DOM overflow
- 2026-08-14：Desktop 白屏真正修复 —— 根因有两个：① Photino.NET 3.0.14 在 Windows 上窗口失效（实测只剩标题栏），升到 4.0.16；② WebView2 只能在 STA 线程创建，.NET 主线程默认 MTA → CreateCoreWebView2Environment 报 0x80010106（RPC_E_CHANGED_MODE）被 Photino 静默吞掉，窗口全白且无任何 webview2 子进程。修复：窗口创建挪到 `Thread.SetApartmentState(STA)` 的专用线程（仅 Windows 设 STA），入口保持同步；关窗后 `StopAsync` 停 Kestrel。用最小 WinForms+WebView2 和 Photino 复现工程对照实验定位（STA 成功渲染、MTA 复现白屏），前两个"修复"提交（同步入口、拷 wwwroot）本身正确但都不是真正原因。实测窗口正常渲染完整 UI（Kestrel 日志可见 WebView2 的 GET / + js/css 200）
- 2026-08-14：文件列表工具栏收拢 + 行多选 —— 「上传/新建目录」收进「操作」下拉（el-dropdown：新建目录/上传/删除三项，上传触发隐藏 file input），工具栏只剩 同步路径/上级/刷新/操作；**删除项为批量删除**（`selectedItems` computed 过滤当前列表选中项，未选中时 disabled 并显示数量「删除（N）」；确认后逐个 `api.remove`（dir 用 parentDir(fullPath)），完成后清空选中并 reload）；行选择改为 Windows/Mac 风格多选：隐藏 selection 列 checkbox（CSS display:none，列宽 1px），`@row-click` 自处理——普通单击单选（clearSelection+toggleRowSelection）、Ctrl/Cmd 单击加减选（toggle 不重置其他）、Shift 单击范围选（lastSelected 到当前行 items 全选中，row-key=fullPath），`rowClassName` 高亮 `.row-selected`（背景 #e3efff）；双击目录/文件行为保留（打开/编辑）。`.fm` 卡片整体 `user-select:none` 屏蔽浏览器原生文本选中（行选择/拖拽不出高亮，实测 getSelection 为空）。实测：单选、Ctrl 加选、Meta 减选、Shift 范围选（logs→test 中间 4 项）、批量删除（btest 内 4 项 + 3 个 txt）全部通过
