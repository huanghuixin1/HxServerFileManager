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

- 2026-08-18：Linux 端两个问题修复 —— ① **下载没有默认文件名**：Photino.Native 的 Linux `ShowSaveFile` 把 defaultPath 当目录调 `gtk_file_chooser_set_current_folder`，传文件名必失败 → 文件名框为空。修复：`Dialogs.SaveFileDialog` 的 Linux 分支改用 `GtkDialogs.SaveFile`（GTK3 P/Invoke 自建保存对话框，`gtk_file_chooser_set_current_name` 预填文件名；覆盖确认/过滤器保留；任何异常回退 Photino 原生对话框）。② **拖拽上传无反应**：WebKitGTK 对系统文件管理器拖入的 HTML5 drag/drop 事件支持有多个未修复 bug（webkit 204281/198915/320301，Photino.Native #152），DOM 事件根本不触发。修复：`GtkDrop`（GTK3 P/Invoke）在 webview 上以普通优先级 `g_signal_connect`（先于 WebKit 内部 connect_after）接管 `drag-motion/drag-leave/drag-drop/drag-data-received`，命中 `text/uri-list` 时 return TRUE 抢占（true_handled 累积器停掉 WebKit handler），解析 file:// 路径经消息桥发 `desktopDragState`（拖入遮罩）/`desktopDrop`（放下）；前端 `onDesktopEvent` 订阅（api.js 新增事件分发，与一次性 desktopOps 并存），FileManager 新增 `active` prop 只让当前激活标签响应；上传由 C# `uploadDropped` 处理——JS 无法读任意本地路径，壳进程代读代传（CollectLocalTree 递归收集目录树 → `/api/ensure-dirs` + multipart POST `/api/upload`，带 Bearer token），进度走 `uploadDroppedProgress`，`uploadDroppedCancel`/「停止上传」可取消（ActiveUploads 注册表）。构建：`cd client && npm run build`（vite 直出 wwwroot）→ `dotnet build HxServerFileManager.Desktop`（csproj 镜像 wwwroot）。坑：C# 顶级语句区（含局部函数）必须在所有类型声明之前，`CollectLocalTree` 放 GtkDrop 类后面会 CS8803；Task.Run lambda 不继承外层流分析，可空变量要先用校验后固化值再捕获；`g_signal_connect_data` 的委托要静态字段持引用防 GC；`(PhotinoWindow)sender!` 消除整文件 CS8600/8604 警告。**⚠ 自建 GTK 对话框必须 `gtk_widget_show_all(dialog)` 再 `gtk_dialog_run`**：`gtk_dialog_run` 内部只 `gtk_widget_show(dialog)`，而 GtkDialog::show 只显示自己的内容区/按钮区，不会递归显示 pack 进去的 GtkFileChooserWidget——漏了 show_all 实测弹出来只有标题“下载文件”+取消/保存按钮，中间选路径区域空白（用户反馈“连选路径的地方都没”），另需 `gtk_window_set_default_size` 给足尺寸。
- 2026-08-18：跨平台下载/导出对话框修复 —— 桌面壳下载/导出/批量下载在 Linux/macOS 上抛 `Unable to load shared library 'comdlg32.dll'`。原因：`Win32Dialogs.SaveFile/PickFolder` 直接 P/Invoke comdlg32/shell32（仅 Windows 存在）。修复：新增 `Win32Dialogs.SaveFileDialog/PickFolderDialog` 封装——Windows 走原 Win32（保留预填文件名、绕开 Photino 保存对话框 bug），Linux/macOS 走 Photino 原生 `ShowSaveFile/ShowOpenFolder`（AppKit/GTK）。注意 Photino filters 类型是 `(string Name, string[] Extensions)[]`；macOS/Linux 对话框**不能预填默认文件名**（Photino API 仅接受 defaultPath 目录，回读路径）。
- 2026-08-18：mac .app 图标修复 —— `.app` 在 Finder/Dock 的图标来自 `Contents/Resources/*.icns` + Info.plist `CFBundleIconFile`，不是运行时 SetIconFile。build.sh 组包时：macOS 优先 iconutil(sips+iconutil)，Windows 用 make-icns.ps1（logo.png 256px → icns 7 尺寸 16~1024）。图标生成逻辑整段 C# Add-Type 内联：PowerShell 5.1 函数返回/接收 byte[] 会被逐字节管道展开（几十万字节撑爆时间），且 `Add-Type -AssemblyName X -TypeDefinition` 参数组合非法、System.Drawing.Common 在 PS5.1 不存在、UTF-8 中文注释在 GBK 控制台会乱码吃掉 C# 代码（C# 注释必须英文）。
- 2026-08-17：mac .app 打不开（「无法打开」）修复 —— Windows 打包丢 Unix 执行位。build.sh 每次 mac-app 组包生成 `fix-mac-$rid.sh`（chmod -R +x → xattr -dr com.apple.quarantine → codesign --force --deep -s - → open），脚本架构无关：循环修复同目录全部 `HxServerFileManager-*.app`（x64/arm 一起放也能一次全修）。macOS 的 AppKit 要求 NSWindow 只能主线程创建（Windows 则必须 STA 线程跑 WebView2）；zip 打包丢执行位/权限，必须 .sh 修复。
