# Project Memory

以后处理这个项目时，先读这个文件。

## 项目信息
- 本项目是.net10实现的webssh服务
- 结构：`Program.cs`（Kestrel 后端，SSH.NET 管理 Linux）+ `client/`（Vue3 + Vite + Element Plus 前端，构建产物输出到 `wwwroot/`）
- 后端默认监听 **5101**（`PORT` 环境变量可覆盖；本机 shell 里 `PORT=0` 时会随机端口，属环境特性不是 bug）
- `test-linux/` 提供 Docker 测试机：`docker compose up -d --build`，映射本机 2222 → 容器 22，`testuser/testpass`

## 长期规则
- 支持PC和手机端浏览（响应式布局，media query 单列切换）

## 重要文件
- `Program.cs`：全部后端逻辑（最小 API + 单文件类型定义）。**JSON body 绑定一律读 `ConnectionId`**（camelCase `connectionId`），前端 `api.js` 必须发 `connectionId` 字段，发 `connId` 会静默绑定为 null（历史教训，见已知问题）
- `client/src/App.vue`：多连接标签页架构。`connections[]` 存所有活跃会话，`activeId` 当前标签；标签可关闭（确认后断开）；工作区用 `v-show` 按连接渲染，保证每会话的终端历史/文件浏览器状态独立
- `client/src/api.js`：所有 /api 封装。POST 类接口统一发 `{ connectionId, ... }`；文件接口参数名与后端 record 一一对应（如 rename 用 `path`+`name`，不是 `dir`/`oldName`）
- `client/src/components/`：ConnectPanel（连接表单，可内联也可在对话框里复用）、SavedConnections、FileManager（el-table/el-breadcrumb）、Terminal、LogPanel（SSE）、EditorModal
- `client/vite.config.js`：dev proxy 指向 `http://localhost:5101`

## 已知问题
- 曾批量出现“字段名不匹配”：前端发 `connId`，后端绑 `ConnectionId`，导致 disconnect/reconnect/mkdir/rename/delete/command/save 全部静默失败或 404。已全部统一为 `connectionId`。**新增后端接口时务必核对 body 字段名**
- 后端最小 API 返回体一律 camelCase（`FileEntry` 序列化为 `name/fullPath/isDirectory/size/lastWriteTimeUtc/isText`），前端 FileManager 曾误读 PascalCase 导致列表空白，已改 camelCase。**前端读响应字段时一律用小写开头**
- 后端会话空闲 30 分钟自动回收（ConnectionManager.CleanupLoop）；前端标签不会自动消失，此时操作会报“连接不存在或已断开”，需手动断开重连
- Docker 端到端（连 test-linux）尚未跑：开发环境无 docker
- `connections.json` 明文存密码/私钥，仅限本地/内网

## 进度记录
- 2026-08-14：前端整体切到 Element Plus（main.js 注册 EP+图标；组件全用 el-*）
- 2026-08-14：修复 5 处字段名 bug（connId→connectionId）、断开/重连、dev proxy 端口
- 2026-08-14：支持多服务器并发连接 —— App.vue 会话标签栏（自定义 pill tab，可关闭+确认）、新建连接对话框、每标签独立 FileManager/Terminal/EditorModal（v-show 保留状态）；后端本就支持多会话（ConcurrentDictionary），无需改动
