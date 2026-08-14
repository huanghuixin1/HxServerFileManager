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
