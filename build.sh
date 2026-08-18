#!/usr/bin/env bash
# ============================================================================
# HxServerFileManager 跨平台发布脚本
#
#   桌面壳（HxServerFileManager.Desktop，Photino.NET WebView）：
#     Windows → WebView2 / macOS → WKWebView / Linux → WebKitGTK
#   可选：后端服务端（HxServerFileManager，Kestrel，常用于 Linux 服务器部署）
#
# 用法：
#   ./build.sh [选项] [target...]
#   不带任何参数双击运行时，会弹出菜单，输入数字选择要编译的目标。
#
# target（缺省：交互终端弹菜单选择；非交互环境 = all，即桌面三平台）：
#   win-x64      Windows 10/11 x64（目标机需 WebView2 Runtime，Win10/11 基本自带）
#   linux-x64    Linux x64（Debian/Ubuntu：sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0）
#   linux-arm64  树莓派 / ARM 服务器（同上依赖）
#   osx-x64      macOS Intel（.NET 需 macOS 12+）
#   osx-arm64    macOS Apple Silicon（.NET 需 macOS 11+）
#   mac-app      macOS 打包成双击可开的 .app 包（RID 默认 osx-arm64，可写 ./build.sh mac-app osx-x64
#                或 ./build.sh mac-app-osx-x64 指定 Intel；产物 dist/HxServerFileManager-<rid>.app，
#                ARM/Intel 双架构可共存、互不覆盖）
#                注意：Windows 上只能组包，签名需拿到 Mac 上执行 codesign（见脚本尾部提示）
#   server       后端服务端（默认 RID=linux-x64，可用 -r 覆盖；也可发布到 Windows）
#   all          构建全部桌面目标（win-x64 + linux-x64 + osx-arm64 普通目录，不含 server 与 mac-app）
#
# 选项：
#   -r RID     server 目标的 RID（默认 linux-x64）
#   -s         自包含发布（内置 .NET 运行时，体积更大但目标机免装 .NET；默认框架依赖）
#   -O         多文件输出（PublishSingleFile=false，默认单文件 + 旁边跟原生 Photino 库）
#   --no-pack  只发布，不打压缩包
#   -o DIR     产物输出目录（默认 ./dist）
#   -h         帮助
#
# 环境变量：
#   HX_FRONTEND=1   发布前先构建前端（需 node/npm）：cd client && npm ci && npm run build
#
# 产物布局：
#   dist/<rid>/            桌面壳（直接双击运行，或拷贝整目录到目标机）
#   dist/<rid>.zip|.tar.gz 打包产物
#   dist/HxServerFileManager-<rid>.app            macOS 应用包（osx-arm64 / osx-x64）
#   dist/HxServerFileManager-macos-<rid>.zip      macOS 应用压缩包
#   dist/server/<rid>/     后端服务端
# ============================================================================
set -euo pipefail

cd "$(dirname "$0")"

DESKTOP_PROJECT="HxServerFileManager.Desktop/HxServerFileManager.Desktop.csproj"
SERVER_PROJECT="HxServerFileManager/HxServerFileManager.csproj"

OUT="dist"
SERVER_RID="linux-x64"
SELF_CONTAINED=0
SINGLE_FILE="true"    # PublishSingleFile 按字符串 true/false 比较，不能用 0/1
PACK=1
TARGETS=()

usage() {
  sed -n '2,44p' "$0" | sed 's/^# \{0,1\}//'
}

die() { echo "错误：$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    win-x64|linux-x64|linux-arm64|osx-x64|osx-arm64)
      TARGETS+=("$1") ;;
    server)  TARGETS+=(server) ;;
    mac-app) TARGETS+=(mac-app) ;;
    mac-app-osx-arm64|mac-app-osx-x64)
      TARGETS+=("$1") ;;
    all)     TARGETS=(win-x64 linux-x64 osx-arm64) ;;
    -r|--rid)    SERVER_RID="$2"; shift ;;
    -s|--self-contained) SELF_CONTAINED=1 ;;
    -O|--no-single-file) SINGLE_FILE="false" ;;
    --no-pack)   PACK=0 ;;
    -o)          OUT="$2"; shift ;;
    -h|--help)   usage; exit 0 ;;
    *)  die "未知参数：$1（./build.sh -h 查看用法）" ;;
  esac
  shift
done

if [[ ${#TARGETS[@]} -eq 0 ]]; then
  if [[ -t 0 ]]; then
    # 无参数 + 交互终端（双击/直接运行）：弹出菜单让用户选择编译目标
    echo
    echo "══════════════════════════════════════════════════"
    echo " HxServerFileManager 构建目标选择"
    echo "══════════════════════════════════════════════════"
    echo "  1) Windows 桌面壳             (win-x64)"
    echo "  2) Linux 桌面壳               (linux-x64)"
    echo "  3) macOS Apple Silicon .app    (mac-app / osx-arm64，推荐)"
    echo "  4) macOS Intel .app            (mac-app / osx-x64)"
    echo "  5) Linux 服务端                (server / linux-x64)"
    echo "  6) 全部桌面四平台              (Windows + Linux + macOS ARM + Intel .app)"
    echo "  7) 全部桌面四平台 + 服务端"
    echo "  0) 退出"
    echo "----------------------------------------------"
    while true; do
      read -r -p "请选择 (0-7): " choice
      case "$choice" in
        1) TARGETS=(win-x64); break;;
        2) TARGETS=(linux-x64); break;;
        3) TARGETS=(mac-app); break;;
        4) TARGETS=(mac-app osx-x64); break;;
        5) TARGETS=(server); break;;
        6) TARGETS=(win-x64 linux-x64 mac-app-osx-arm64 mac-app-osx-x64); break;;
        7) TARGETS=(win-x64 linux-x64 mac-app-osx-arm64 mac-app-osx-x64 server); break;;
        0) echo "已退出"; exit 0;;
        *) echo "无效输入，请重新选择。";;
      esac
    done
  else
    # 非交互（管道/CI）：保持默认全部桌面三平台
    TARGETS=(win-x64 linux-x64 osx-arm64)
  fi
fi

# mac-app 目标支持第二个位置参数指定 RID：./build.sh mac-app osx-x64
MAC_RID="osx-arm64"
if [[ "${TARGETS[0]}" == "mac-app" && ${#TARGETS[@]} -gt 1 && "${TARGETS[1]}" =~ ^osx-(x64|arm64)$ ]]; then
  MAC_RID="${TARGETS[1]}"
  TARGETS=(mac-app)
fi

# 前端产物（wwwroot）是镜像进发布目录的，先构建保证最新
if [[ "${HX_FRONTEND:-0}" == "1" ]]; then
  echo "▶ 构建前端（client → wwwroot）"
  ( cd client && npm ci && npm run build )
fi

sc_flag=("--self-contained" "false")
[[ "$SELF_CONTAINED" == 1 ]] && sc_flag=("--self-contained" "true")

SC_NOTE="框架依赖（目标机需安装 .NET 10 运行时：dotnet --list-runtimes 检查）"
[[ "$SELF_CONTAINED" == 1 ]] && SC_NOTE="自包含（目标机免装 .NET）"

publish_desktop() {
  local rid="$1"
  local outdir="$OUT/$rid"
  echo
  echo "══════════════════════════════════════════════════"
  if [[ "$rid" == osx-* ]]; then
    echo "▶ 发布桌面壳：$rid  (自包含（目标机免装 .NET）)"
  else
    echo "▶ 发布桌面壳：$rid  ($SC_NOTE)"
  fi
  echo "══════════════════════════════════════════════════"
  # 先清空再发布：dotnet publish 重复发布到同一目录（DeleteExistingFiles）会把
  # 已存在但不在当前发布列表的文件清掉，也可能因中途失败留下不完整产物（缺原生库等）。
  # 干净目录发布保证产物完整、无残留（mac-app 用 staging 目录也是同一原因）。
  rm -rf "$outdir"
  # mac 目标固定自包含发布：目标 Mac 免装 .NET 运行时，不受 -s 开关影响。
  # （框架依赖的 mac 包在没有装 .NET 的机器上双击会报"这台 Mac 不支持此应用程序"，毫无意义）
  local sc=("${sc_flag[@]}")
  [[ "$rid" == osx-* ]] && sc=("--self-contained" "true")
  dotnet publish "$DESKTOP_PROJECT" -c Release -r "$rid" \
    "${sc[@]}" \
    -p:PublishSingleFile="$SINGLE_FILE" \
    -p:IncludeNativeLibrariesForSelfExtract=false \
    -o "$outdir"
  local bin="$outdir/HxServerFileManager.Desktop"
  [[ -f "$bin" ]] && chmod +x "$bin"   # Windows 无执行位，补上便于 tar/zip 传输

  # Linux 桌面壳：额外生成 .desktop 启动器（图中双击入口）。
  # Linux 桌面应用不靠双击裸二进制启动（GNOME 会当文本打开/拒绝运行），
  # 需要 .desktop 启动器（相当于 Windows .lnk / macOS .app）。
  # ⚠️ 启动器文件名避开 "HxServerFileManager.Desktop"：Windows 构建机文件系统
  #    大小写不敏感，同名不同大小写会直接把编译出的二进制覆盖掉（实测踩坑）。
  # 这里生成两个版本：
  #   hxsfm.desktop            —— 相对路径版，与二进制同目录使用（拷贝整个文件夹即可）；
  #   install-desktop.sh       —— 一键装到桌面 + 应用菜单，自动写绝对路径并设信任标记。
  if [[ "$rid" == linux-* ]]; then
    # 应用图标：Linux 没有可执行文件内嵌图标机制，桌面图标必须靠 .desktop 的 Icon= 引用一个
    # 图标文件，这里把 logo.png 一并放进发布目录（GNOME/gdk-pixbuf 对 .ico 支持差，用 PNG）。
    cp "logo.png" "$outdir/logo.png"

    # 注意 Icon= 用主题名 hxsfm 而非相对路径：.desktop 规范只认主题图标名或绝对路径，
    # 相对路径解析不了（应用首次启动会自装主题图标，见 Desktop/Program.cs InstallLinuxIcon）
    cat > "$outdir/hxsfm.desktop" <<'DESK'
[Desktop Entry]
Type=Application
Version=1.0
Name=HxServerFileManager
GenericName=SSH File Manager
Comment=基于 Kestrel + SSH.NET 的服务器文件管理 / WebSSH
Exec=sh -c 'cd "$(dirname "$1")" && exec ./HxServerFileManager.Desktop' sh %k
Icon=hxsfm
StartupWMClass=HxServerFileManager.Desktop
Terminal=false
Categories=Network;FileManager;Development;
StartupNotify=false
DESK
    chmod +x "$outdir/hxsfm.desktop"

    cat > "$outdir/install-desktop.sh" <<'SH'
#!/usr/bin/env bash
# 一键把 HxServerFileManager 安装到「桌面 + 应用菜单」。
# 用法：./install-desktop.sh    （在应用文件夹内执行）
set -euo pipefail
APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$APP_DIR/HxServerFileManager.Desktop"
if [[ ! -f "$BIN" ]]; then
  echo "错误：未找到 $BIN" >&2
  exit 1
fi
# 跨平台拷贝（zip/Windows 传输等）经常丢失可执行位，这里直接补上，不需要用户手动 chmod
chmod +x "$BIN"
# 同目录的便携版启动器也要可执行位（GNOME 只运行带 +x 的 .desktop）
if [[ -f "$APP_DIR/hxsfm.desktop" ]]; then chmod +x "$APP_DIR/hxsfm.desktop"; fi

# 找桌面目录（XDG 惯例 + 回退 ~/Desktop）
DESKTOP_DIR="${XDG_DESKTOP_DIR:-}"
if [[ -z "$DESKTOP_DIR" ]]; then
  if command -v xdg-user-dir >/dev/null 2>&1; then
    DESKTOP_DIR="$(xdg-user-dir DESKTOP)"
  fi
fi
[[ -n "$DESKTOP_DIR" ]] || DESKTOP_DIR="$HOME/Desktop"
mkdir -p "$DESKTOP_DIR"

# 把图标装入用户图标主题：.desktop 的 Icon= 只认主题图标名或绝对路径，主题名最可靠
ICON_DIR="$HOME/.local/share/icons/hicolor"
for s in 256 128 64 48 32; do
  mkdir -p "$ICON_DIR/${s}x${s}/apps"
  cp "$APP_DIR/logo.png" "$ICON_DIR/${s}x${s}/apps/hxsfm.png"
done
gtk-update-icon-cache -f -t "$ICON_DIR" >/dev/null 2>&1 || true

cat > "$DESKTOP_DIR/HxServerFileManager.desktop" <<DESKCAT
[Desktop Entry]
Type=Application
Version=1.0
Name=HxServerFileManager
GenericName=SSH File Manager
Comment=基于 Kestrel + SSH.NET 的服务器文件管理 / WebSSH
Exec="$BIN"
Icon=hxsfm
StartupWMClass=HxServerFileManager.Desktop
Terminal=false
Categories=Network;FileManager;Development;
StartupNotify=false
DESKCAT
chmod +x "$DESKTOP_DIR/HxServerFileManager.desktop"

# GNOME 的「运行前确认」信任标记
if command -v gio >/dev/null 2>&1; then
  gio set "$DESKTOP_DIR/HxServerFileManager.desktop" metadata::trusted true || true
fi

# 一并注册到应用菜单（可选）
if command -v desktop-file-install >/dev/null 2>&1 && [[ -w /usr/share/applications ]]; then
  desktop-file-install --dir=/usr/share/applications "$DESKTOP_DIR/HxServerFileManager.desktop" || true
fi

echo "✔ 已生成桌面启动器：$DESKTOP_DIR/HxServerFileManager.desktop"
echo "  双击即可打开（GNOME 若提示，点「允许启动」即可）"
SH
    chmod +x "$outdir/install-desktop.sh"
    echo "  ✔ 已生成：$outdir/hxsfm.desktop（同目录相对版，双击它即可）"
    echo "  ✔ 已生成：$outdir/install-desktop.sh（一键装到桌面+应用菜单）"
  fi
  pack "$rid"
}

build_server() {
  local outdir="$OUT/server/$SERVER_RID"
  echo
  echo "══════════════════════════════════════════════════"
  echo "▶ 发布后端服务端：$SERVER_RID  ($SC_NOTE)"
  echo "══════════════════════════════════════════════════"
  dotnet publish "$SERVER_PROJECT" -c Release -r "$SERVER_RID" \
    "${sc_flag[@]}" \
    -o "$outdir"
  local bin="$outdir/HxServerFileManager"
  [[ -f "$bin" ]] && chmod +x "$bin"   # Windows 构建产物默认无执行位，补上便于直接部署到 Linux

  # Linux 服务端：自动带上启动脚本（后台启动/停止/重启/状态），并补执行位。
  # Windows 服务端不适用 bash 脚本，跳过。
  if [[ "$SERVER_RID" == linux-* ]]; then
    cp "start-linux.sh" "$outdir/start-linux.sh"
    chmod +x "$outdir/start-linux.sh"
    echo "  ✔ 已带入：$outdir/start-linux.sh（后台启动 ./start-linux.sh，详见脚本头部说明）"
  fi

  # 如需带 env.json 部署，可在此拷贝 configs/env.json（env.json 默认不随构建复制，仅 example）
}

pack() {
  [[ "$PACK" == 1 ]] || return
  local rid="$1"
  echo "▶ 打包 dist/$rid"
  if command -v zip >/dev/null 2>&1; then
    ( cd "$OUT" && zip -qr "$rid.zip" "$rid" )
  elif tar --version 2>/dev/null | grep -qi bsdtar; then
    ( cd "$OUT" && tar -a -cf "$rid.zip" "$rid" )
  else
    ( cd "$OUT" && tar -czf "$rid.tar.gz" "$rid" )
  fi
}

# macOS .app 包：Contents/Info.plist + Contents/MacOS/{单文件程序, Photino.Native.dylib, wwwroot, configs, libs}
# wwwroot/Data 等由 ContentRoot 解析逻辑定位（Program.cs 优先取可执行文件所在目录），
# 双击启动（cwd=/）也能找到前端页面。签名与去隔离标记必须在 Mac 上执行（见 build_mac_app 尾部提示）。
build_mac_app() {
  local rid="${1:-$MAC_RID}"
  local app="$OUT/HxServerFileManager-$rid.app"
  local bin_dir="$app/Contents/MacOS"
  # 先发到全新 staging 目录再拷贝组装：dotnet publish 重复发布同一目录时（DeleteExistingFiles）
  # 会把 configs/Photino.Native.dylib 等"已存在但不在当前发布列表"的文件清掉，仅 staging 可避免。
  local stage="$OUT/.mac-stage-$rid"
  echo
  echo "══════════════════════════════════════════════════"
  echo "▶ 打包 macOS .app：$rid  (自包含（目标机免装 .NET）)"
  echo "══════════════════════════════════════════════════"
  rm -rf "$stage"
  # mac 目标固定自包含发布（免装 .NET 运行时），不受 -s 开关影响
  dotnet publish "$DESKTOP_PROJECT" -c Release -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile="$SINGLE_FILE" \
    -p:IncludeNativeLibrariesForSelfExtract=false \
    -o "$stage"

  # 组装 .app
  rm -rf "$app"
  mkdir -p "$app/Contents/Resources"
  cp -r "$stage" "$bin_dir"

  # 精简：发布目录中的 .pdb 不随 .app 分发
  rm -f "$bin_dir/HxServerFileManager.Desktop.pdb"

  local bin="$bin_dir/HxServerFileManager.Desktop"
  [[ -f "$bin" ]] && chmod +x "$bin"
  rm -rf "$stage"
  cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>HxServerFileManager</string>
  <key>CFBundleDisplayName</key>
  <string>HxServerFileManager</string>
  <key>CFBundleIdentifier</key>
  <string>com.hxserverfilemanager.desktop</string>
  <key>CFBundleExecutable</key>
  <string>HxServerFileManager.Desktop</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0.0</string>
  <key>CFBundleVersion</key>
  <string>1</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
</dict>
</plist>
PLIST

  # 把图标放这里则补上 CFBundleIconFile 键；仓库暂无 .icns，先用通用图标。
  # 打包成 zip（在 Windows 上压缩会丢执行位/权限，解压后按下方提示 chmod + codesign）
  echo "▶ 打包 $app"
  local appname="HxServerFileManager-$rid.app"
  if [[ "$PACK" == 1 ]]; then
    if command -v zip >/dev/null 2>&1; then
      ( cd "$OUT" && zip -qr "HxServerFileManager-macos-$rid.zip" "$appname" )
    elif tar --version 2>/dev/null | grep -qi bsdtar; then
      ( cd "$OUT" && tar -a -cf "HxServerFileManager-macos-$rid.zip" "$appname" )
    else
      ( cd "$OUT" && tar -czf "HxServerFileManager-macos-$rid.tar.gz" "$appname" )
    fi
  fi

  echo
  echo "⚠️   到 Mac 上执行（Apple Silicon 上未签名 arm64 程序会被内核直接杀掉，必须签）：
  cd $OUT
  chmod +x $app/Contents/MacOS/HxServerFileManager.Desktop
  codesign --force --deep -s - $app
  # 若是从网络下载得到的，还需去掉隔离标记：
  xattr -dr com.apple.quarantine $app
  # 然后双击 $app 即可（或 open $app）
  # 可选：正式分发用真证书替换 -s - ；把图标 .icns 放进 Contents/Resources 并在 Info.plist 加 CFBundleIconFile"
}

for t in "${TARGETS[@]}"; do
  if [[ "$t" == "server" ]]; then build_server
  elif [[ "$t" == "mac-app" ]]; then build_mac_app "$MAC_RID"
  elif [[ "$t" == mac-app-osx-* ]]; then build_mac_app "${t#mac-app-}"
  else publish_desktop "$t"
  fi
done

echo
echo "══════════════════════════════════════════════════"
echo "✅ 全部完成，产物在 ./$OUT"
echo "══════════════════════════════════════════════════"
echo
echo "各平台注意："
echo "  Windows (win-x64) : 目标机需 WebView2 Runtime（Win10/11 通常已装）"
echo "  Linux (linux-*)   : sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0 libglib2.0-0"
echo "                      （若窗口空白，先试 WEBKIT_DISABLE_COMPOSITING_MODE=1 ./HxServerFileManager.Desktop）"
echo "  macOS (osx-* / mac-app) : 自包含发布，目标机无需安装 .NET；但架构必须匹配——Apple Silicon 用 osx-arm64，Intel 用 osx-x64，"
echo "                          架构不对双击会报「这台 Mac 不支持此应用程序」。"
echo "                          .app（dist/HxServerFileManager-<rid>.app）需在 Mac 上 codesign --force --deep -s - 后双击（Apple Silicon 必须）"
