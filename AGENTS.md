# Project Memory

以后处理这个项目时，先读这个文件。

## 项目信息

## 长期规则
- 支持PC和手机端浏览（响应式布局，media query 单列切换）
- hx项目是指HxServerFileManager
- desk项目是指HxServerFileManager.Desktop
## 重要文件

- `build.sh`：跨平台构建/打包（win-x64 / linux-x64 / mac-app(osx-arm64,osx-x64) / server），macOS 组包只可能发生在 Windows/Linux 上；mac 的 .app 用 zip 传递（Windows 压缩丢执行位，需 fix-mac-*.sh：chmod + xattr + codesign）
- `make-icns.ps1`：Windows 上无 iconutil，用 System.Drawing+C# Add-Type 生成 mac .app 的 Contents/Resources/logo.icns（Info.plist CFBundleIconFile=logo）
- `HxServerFileManager.Desktop/Program.cs`：Photino 壳；Windows 用 Win32 GetSaveFileName（comdlg32 P/Invoke，绕开 Photino Windows 保存对话框 HRESULT bug），**Linux/macOS 用 Photino 原生 ShowSaveFile/ShowOpenFolder**（跨平台对话框封装在 `Dialogs.SaveFileDialog/PickFolderDialog`，不要直接调 Win32 P/Invoke，否则 comdlg32.dll 不存在直接抛 DllNotFoundException）
- `HxServerFileManager/client/src/App.vue`：前端（hx 项目路径是 HxServerFileManager/client），桌面壳下载/导出走 `window.external.sendMessage`

## 进度记录

- 2026-08-18：跨平台下载/导出对话框修复 —— 桌面壳下载/导出/批量下载在 Linux/macOS 上抛 `Unable to load shared library 'comdlg32.dll'`。原因：`Win32Dialogs.SaveFile/PickFolder` 直接 P/Invoke comdlg32/shell32（仅 Windows 存在）。修复：新增 `Win32Dialogs.SaveFileDialog/PickFolderDialog` 封装——Windows 走原 Win32（保留预填文件名、绕开 Photino 保存对话框 bug），Linux/macOS 走 Photino 原生 `ShowSaveFile/ShowOpenFolder`（AppKit/GTK）。注意 Photino filters 类型是 `(string Name, string[] Extensions)[]`；macOS/Linux 对话框**不能预填默认文件名**（Photino API 仅接受 defaultPath 目录，回读路径）。
- 2026-08-18：mac .app 图标修复 —— `.app` 在 Finder/Dock 的图标来自 `Contents/Resources/*.icns` + Info.plist `CFBundleIconFile`，不是运行时 SetIconFile。build.sh 组包时：macOS 优先 iconutil(sips+iconutil)，Windows 用 make-icns.ps1（logo.png 256px → icns 7 尺寸 16~1024）。图标生成逻辑整段 C# Add-Type 内联：PowerShell 5.1 函数返回/接收 byte[] 会被逐字节管道展开（几十万字节撑爆时间），且 `Add-Type -AssemblyName X -TypeDefinition` 参数组合非法、System.Drawing.Common 在 PS5.1 不存在、UTF-8 中文注释在 GBK 控制台会乱码吃掉 C# 代码（C# 注释必须英文）。
- 2026-08-17：mac .app 打不开（「无法打开」）修复 —— Windows 打包丢 Unix 执行位。build.sh 每次 mac-app 组包生成 `fix-mac-$rid.sh`（chmod -R +x → xattr -dr com.apple.quarantine → codesign --force --deep -s - → open），脚本架构无关：循环修复同目录全部 `HxServerFileManager-*.app`（x64/arm 一起放也能一次全修）。macOS 的 AppKit 要求 NSWindow 只能主线程创建（Windows 则必须 STA 线程跑 WebView2）；zip 打包丢执行位/权限，必须 .sh 修复。
