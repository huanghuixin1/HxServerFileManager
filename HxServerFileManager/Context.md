# HxServerFileManager · 上下文简报

> 更新：2026-08-14 ｜ 控制台项目（Exe）+ Kestrel + SSH.NET 管理 Linux 服务器

## 架构
- 后端：.NET 10 控制台，`FrameworkReference AspNetCore` 启用 Kestrel；SSH.NET 连接 Linux（SFTP + SSH）。监听默认 **5101**（`PORT` 可改）。
- 前端：Vue3 + Vite 工程（`client/`），构建产物输出到 `../wwwroot`。

## 已完成
- ✅ 后端三增强：连接信息持久化到 `Data/connections.json`、文本文件 `GET/PUT /api/file-content` 在线编辑、`GET /api/logs/stream` SSE 实时日志。编译+启动已验证。
- ✅ 前端已全部改为 Element Plus：`main.js` 注册 EP + 全部图标；6 组件 + `App.vue` 使用 `el-form/el-table/el-dialog/el-breadcrumb/el-button/el-tag` 等。
- ✅ `package.json` 已声明 `element-plus ^2.14.4` + `@element-plus/icons-vue ^2.3.2`。
- ✅ 后端已加 `MapFallbackToFile("index.html")`。
- ✅ `npm run build` 已覆盖 `wwwroot`（index.html + assets/，旧 app.js/style.css 已被清空）。
- ✅ **多服务器并发连接**：`App.vue` 会话标签栏（自定义 pill tab，可关闭+确认）、新建连接对话框、每标签独立 FileManager/Terminal/编辑器（`v-show` 保留各自终端历史/目录状态）。后端本就支持多会话，无需改动。

## 文件列表空白 bug 已修（字段大小写）
- 后端 `/api/files` 的 `FileEntry` 经最小 API 序列化为 camelCase（`name/fullPath/isDirectory/size/lastWriteTimeUtc/isText`），前端 FileManager 原来读 PascalCase，导致名称/大小/修改时间全空白。已统一改读 camelCase。

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
`connections.json` 明文存密码/私钥，仅限本地/内网。

## 运行
```bash
dotnet run                      # 后端 http://localhost:5101
cd client && npm run build      # 前端产物 -> ../wwwroot
```
