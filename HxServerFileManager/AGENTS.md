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

## 已知问题
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
- 2026-08-14：工作区高度修复 —— `.workspace` grid 只有 columns 没有 rows，子项 `height:100%` 参照不到确定行高回退成内容高度，导致 FileManager/Terminal 卡片矮半截（.term 曾只有 119px、xterm 显示区 37px）。修复：`grid-template-rows: minmax(0,1fr)`（窄屏 media query 改 `1fr 1fr` 两行均分）；xterm 补 `.xterm-viewport height:100%`；`openInteractive` 加 `waitForSize()` 等容器有实际尺寸再算 pty 行列数。双列实测 .fm/.term 均 256px 撑满。**xterm 6 高度链**：`.xterm`（height:100%）→ `.xterm-viewport`（absolute+100%）→ `.xterm-scrollable-element`（**必须 height:100%**，xterm.css 未给它高度，否则保持内容高度溢出、滚动失效）→ `.xterm-screen`（100%）；`.xterm-rows`/screen 内容行由 xterm 内部 buffer 管理，scrollback 滚动靠 xterm setScrollPosition 重渲染，不依赖 DOM overflow
