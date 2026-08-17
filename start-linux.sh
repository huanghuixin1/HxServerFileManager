#!/usr/bin/env bash
# ============================================================================
# HxServerFileManager 后端服务 Linux 启动脚本
#
# 用法（在脚本所在目录执行，或用绝对路径调用）：
#   ./start-linux.sh                  # 默认后台启动（日志写 hxsfm.log，PID 写 hxsfm.pid）
#   ./start-linux.sh 8080             # 指定端口（等价 PORT=8080）
#   ./start-linux.sh -f               # 前台启动，按 Ctrl+C 停止（优雅停机）
#   ./start-linux.sh stop             # 停止后台服务（SIGTERM 优雅停机）
#   ./start-linux.sh restart [port]   # 重启（停止后重新后台启动）
#   ./start-linux.sh status           # 查看运行状态
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
#   1) 同级目录的 HxServerFileManager 可执行文件（发布产物直接这样放，./build.sh server 会自动带上本脚本）
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
    [[ -f "$c" ]] || continue
    # Windows 上构建的产物经常丢失执行位（tar/zip 传输也会丢），这里直接补上，避免“未找到可执行文件”
    [[ -x "$c" ]] || chmod +x "$c"
    echo "$c"
    return 0
  done
  return 1
}

BIN="$(find_binary)" || {
  echo "错误：未找到 HxServerFileManager 可执行文件。" >&2
  echo "      请先构建：./build.sh server  或  把发布产物放在本脚本同目录。" >&2
  exit 1
}

# 参数解析：动作（start/stop/restart/status）+ 端口
ACTION="start"
FOREGROUND=0
PORT_ARG=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    -f|--foreground) FOREGROUND=1 ;;
    stop)    ACTION="stop" ;;
    status)  ACTION="status" ;;
    restart) ACTION="restart" ;;
    -h|--help) sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) PORT_ARG="$1" ;;
  esac
  shift
done

PORT="${PORT:-${PORT_ARG:-15511}}"
export PORT

PIDFILE="$SCRIPT_DIR/hxsfm.pid"
LOGFILE="$SCRIPT_DIR/hxsfm.log"

is_running() {
  [[ -f "$PIDFILE" ]] || return 1
  local pid
  pid="$(cat "$PIDFILE" 2>/dev/null || true)"
  [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null
}

stop_server() {
  if ! is_running; then
    echo "未在运行（无有效 PID 文件：$PIDFILE）"
    rm -f "$PIDFILE"
    return 0
  fi
  local pid
  pid="$(cat "$PIDFILE" 2>/dev/null || true)"
  echo "▶ 停止 HxServerFileManager（PID $pid）…"
  kill "$pid" 2>/dev/null || true
  # 等最多 ~6 秒优雅退出（SIGTERM 触发 ApplicationStopping），超时才强杀
  for _ in {1..30}; do
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.2
  done
  if kill -0 "$pid" 2>/dev/null; then
    echo "  等待超时，强制结束"
    kill -9 "$pid" 2>/dev/null || true
  fi
  rm -f "$PIDFILE"
  echo "✔ 已停止"
}

status_server() {
  if is_running; then
    echo "运行中：PID $(cat "$PIDFILE")，端口 $PORT"
  else
    echo "未运行"
  fi
}

start_server() {
  if is_running; then
    echo "已在运行（PID $(cat "$PIDFILE")），如需重启请用 restart"
    exit 0
  fi

  cd "$(dirname "$BIN")"

  if [[ "$FOREGROUND" == 1 ]]; then
    echo "▶ 前台启动 HxServerFileManager（端口 $PORT）"
    echo "  按 Ctrl+C 停止（优雅停机）"
    exec "$BIN"
  fi

  # 后台启动：nohup 脱离终端，输出写日志，PID 记入 pid 文件
  nohup "$BIN" >>"$LOGFILE" 2>&1 &
  echo $! > "$PIDFILE"
  echo "▶ 已后台启动 HxServerFileManager（端口 $PORT，PID $(cat "$PIDFILE")）"
  echo "  日志：$LOGFILE"
  echo "  停止：$SCRIPT_DIR/start-linux.sh stop"

  # 稍等确认进程存活（端口占用/配置错误会立即退出，此时报错并给日志尾部）
  sleep 1
  if is_running; then
    echo "  状态：运行中 ✔"
  else
    echo "  状态：启动失败？日志尾部如下：" >&2
    tail -20 "$LOGFILE" 2>/dev/null >&2 || true
    rm -f "$PIDFILE"
    exit 1
  fi
}

case "$ACTION" in
  stop)    stop_server ;;
  status)  status_server ;;
  restart) stop_server; FOREGROUND=0; start_server ;;
  start)   start_server ;;
esac
