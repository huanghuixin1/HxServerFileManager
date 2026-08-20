<img width="1539" height="843" alt="image" src="https://github.com/user-attachments/assets/b32f65c8-880f-4cf9-be4f-62e44bead7d8" />

# HxServerFileManager

基于 .NET 10 的 WebSSH 服务器文件管理工具。浏览器打开即用，也提供 Photino 桌面壳（Windows / macOS / Linux），PC 与手机端响应式布局。

`注：MAC端只在x86的虚拟机测过，arm没测过。但理论上应该都没问题`

## 功能

- **多服务器 SSH**：标签页管理多个连接，交互式终端（xterm.js，支持 nano/vim）+ 快捷命令模式，命令历史
- **文件管理**：浏览/上传（含拖拽、文件夹）/下载/批量下载/编辑/重命名/删除，服务器间直传（scp）
- **系统状态**：CPU / 内存 / 磁盘 / 实时网络上下行，操作日志实时推送（SSE）
- **安全**：登录鉴权 + Bearer Token，连接凭据本地加密存储

## 快速开始

后端默认监听 `15511`（`PORT` 环境变量可覆盖）：

```bash
dotnet run --project HxServerFileManager
# 浏览器打开 http://localhost:15511
```

前端开发模式（HMR，代理到 15511）：

```bash
cd HxServerFileManager/client
npm install && npm run dev
```

未配置密码时仅本机回环可访问（fail-closed）。设置登录密码：

```bash
export HXSFM_WEB_PASSWORD="your-password"
# 或写入 configs/env.json（模板见 configs/env.json.example）
```

## 跨平台打包

`build.sh` 一键发布桌面壳与服务端（单文件 + Photino 原生库，双击运行）：

```bash
./build.sh            # 交互菜单
./build.sh win-x64 linux-x64 osx-arm64 mac-app server
./build.sh mac-app osx-x64   # macOS Intel 组包（Windows 上可打，需到 mac 上签名）
```

产物在 `dist/<rid>/`；macOS 为 `dist/HxServerFileManager-<rid>.app`。Linux 依赖：`sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0`。

## 项目结构

```
HxServerFileManager/           后端（Kestrel + SSH.NET）+ 前端 client/（Vue3 + Vite）
HxServerFileManager.Desktop/   桌面壳（Photino.NET）
build.sh / build.bat           跨平台构建脚本
```

## 环境变量

| 变量 | 说明 |
| --- | --- |
| `PORT` | 监听端口，默认 15511 |
| `HXSFM_WEB_PASSWORD` | 登录密码（优先于 env.json） |
| `HXSFM_MAX_UPLOAD_MB` | 上传大小上限 MB，0 不限制，默认 1024 |
