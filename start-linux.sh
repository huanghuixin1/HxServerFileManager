#!/usr/bin/env bash
# ============================================================================
# HxServerFileManager 后端服务 Linux 启动脚本
#
# 用法（在脚本所在目录执行，或用绝对路径调用）：
#   ./start-linux.sh            # 前台启动，按 Ctrl+C 停止（优雅停机）
#   ./start-linux.sh 8080       # 指定端口（等价 PORT=8080）
#   nohup ./start-linux.sh &    # 后台运行（此时用 kill <pid> / SIGTERM 停止）
#
# 可选环境变量（也可写入同目录 .env，脚本自动加载）：
#   PORT                监听端口，默认 15511
#   HXSFM_MAX_UPLOAD_MB  单文件上传上限（MB），默认 1024（1GB），0 = 不限制；
#                        也可写入 configs/env.json 的 maxUploadMb（环境变量优先）
#   HXSFM_WEB_PASSWORD  网页访问密码（不设则仅本机回环可访问）
#   HXSFM_DATA_KEY      连接数据加密主密钥（不设则用 Data/secret.key）
#   HXSFM_CONTENT_ROOT  ContentRoot（默认取可执行文件所在目录）
#
# 脚本自动定位：
#   1) 同级目录的 HxServerFileManager 可执行文件（发布产物直接这样放）
#   2) 仓库内 ./dist/server/linux-x64/HxServerFileManager（./build.sh server 产物）
#   3) 开发构建 ./HxServerFileManager/bin/Debug/net10.0/HxServerFileManager
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# 加载同目录 .env（若有），不覆盖已导出的环境变量
if [[ -f "$SCRIPT_DIR/.env" ]]; then
  while IFS='=' read -r k v; do
    [[ -z "$k" || "$k" == \#* ]] && continue
    export "${k%%[[:space:]]*}"="${v//\"/}"
  done < "$SCRIPT_DIR/.env"
fi

find_binary() {
  local candidates=(
    "$SCRIPT_DIR/HxServerFileManager"
    "$SCRIPT_DIR/dist/server/linux-x64/HxServerFileManager"
    "$SCRIPT_DIR/HxServerFileManager/bin/Debug/net10.0/HxServerFileManager"
  )
  for c in "${candidates[@]}"; do
    [[ -x "$c" ]] && { echo "$c"; return 0; }
  done
  return 1
}

BIN="$(find_binary)" || {
  echo "错误：未找到 HxServerFileManager 可执行文件。" >&2
  echo "      请先构建：./build.sh server  或  把发布产物放在本脚本同目录。" >&2
  exit 1
}

PORT="${PORT:-${1:-15511}}"
export PORT

echo "▶ HxServerFileManager 启动（端口 $PORT）"
echo "  ContentRoot: $(dirname "$BIN")"
echo "  按 Ctrl+C 停止（优雅停机）"

cd "$(dirname "$BIN")"
exec "$BIN"
