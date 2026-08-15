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
#
# target（缺省 = all，即桌面三平台）：
#   win-x64      Windows 10/11 x64（目标机需 WebView2 Runtime，Win10/11 基本自带）
#   linux-x64    Linux x64（Debian/Ubuntu：sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0）
#   linux-arm64  树莓派 / ARM 服务器（同上依赖）
#   osx-x64      macOS Intel（.NET 需 macOS 12+）
#   osx-arm64    macOS Apple Silicon（.NET 需 macOS 11+）
#   mac-app      macOS 打包成双击可开的 .app 包（RID 默认 osx-arm64，可写 ./build.sh mac-app osx-x64）
#                注意：Windows 上只能组包，签名需拿到 Mac 上执行 codesign（见脚本尾部提示）
#   server       后端服务端（默认 RID=linux-x64，可用 -r 覆盖；也可发布到 Windows）
#   all          构建上面全部桌面目标（不包含 server 与 mac-app）
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
  sed -n '2,38p' "$0" | sed 's/^# \{0,1\}//'
}

die() { echo "错误：$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    win-x64|linux-x64|linux-arm64|osx-x64|osx-arm64)
      TARGETS+=("$1") ;;
    server)  TARGETS+=(server) ;;
    mac-app) TARGETS+=(mac-app) ;;
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
  TARGETS=(win-x64 linux-x64 osx-arm64)
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
  echo "▶ 发布桌面壳：$rid  ($SC_NOTE)"
  echo "══════════════════════════════════════════════════"
  dotnet publish "$DESKTOP_PROJECT" -c Release -r "$rid" \
    "${sc_flag[@]}" \
    -p:PublishSingleFile="$SINGLE_FILE" \
    -o "$outdir"
  local bin="$outdir/HxServerFileManager.Desktop"
  [[ -f "$bin" ]] && chmod +x "$bin"   # Windows 无执行位，补上便于 tar/zip 传输
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
  local rid="$MAC_RID"
  local app="$OUT/HxServerFileManager.app"
  local bin_dir="$app/Contents/MacOS"
  # 先发到全新 staging 目录再拷贝组装：dotnet publish 重复发布同一目录时（DeleteExistingFiles）
  # 会把 configs/Photino.Native.dylib 等"已存在但不在当前发布列表"的文件清掉，仅 staging 可避免。
  local stage="$OUT/.mac-stage-$rid"
  echo
  echo "══════════════════════════════════════════════════"
  echo "▶ 打包 macOS .app：$rid  ($SC_NOTE)"
  echo "══════════════════════════════════════════════════"
  rm -rf "$stage"
  dotnet publish "$DESKTOP_PROJECT" -c Release -r "$rid" \
    "${sc_flag[@]}" \
    -p:PublishSingleFile="$SINGLE_FILE" \
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
  if [[ "$PACK" == 1 ]]; then
    if command -v zip >/dev/null 2>&1; then
      ( cd "$OUT" && zip -qr "HxServerFileManager-macos-$rid.zip" HxServerFileManager.app )
    elif tar --version 2>/dev/null | grep -qi bsdtar; then
      ( cd "$OUT" && tar -a -cf "HxServerFileManager-macos-$rid.zip" HxServerFileManager.app )
    else
      ( cd "$OUT" && tar -czf "HxServerFileManager-macos-$rid.tar.gz" HxServerFileManager.app )
    fi
  fi

  echo
  echo "⚠️   到 Mac 上执行（Apple Silicon 上未签名 arm64 程序会被内核直接杀掉，必须签）：
  cd $OUT
  chmod +x HxServerFileManager.app/Contents/MacOS/HxServerFileManager.Desktop
  codesign --force --deep -s - HxServerFileManager.app
  # 若是从网络下载得到的，还需去掉隔离标记：
  xattr -dr com.apple.quarantine HxServerFileManager.app
  # 然后双击 HxServerFileManager.app 即可（或 open HxServerFileManager.app）
  # 可选：正式分发用真证书替换 -s - ；把图标 .icns 放进 Contents/Resources 并在 Info.plist 加 CFBundleIconFile"
}

for t in "${TARGETS[@]}"; do
  if [[ "$t" == "server" ]]; then build_server
  elif [[ "$t" == "mac-app" ]]; then build_mac_app
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
echo "  macOS (osx-* / mac-app) : 目标机需 macOS + .NET 10 运行时（-s 自包含则免装）；"
echo "                          .app 需在 Mac 上 codesign --force --deep -s - 后双击（Apple Silicon 必须）"