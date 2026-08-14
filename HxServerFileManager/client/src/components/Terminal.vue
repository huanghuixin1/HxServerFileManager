<script setup>
import { ref, nextTick, watch, onMounted, onUnmounted } from 'vue'
import { Terminal as XTerm } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'
import { api } from '../api.js'

// 两种模式：
//   exec        —— 快捷命令：一次一命令，带 cwd 持久化 + 文件列表联动
//   interactive —— 交互终端：SSH shell + pty（xterm.js），可跑 nano/vim/需要输入的脚本
const props = defineProps({
  connId: String,
  cwd: { type: String, default: '/' },
})
const emit = defineEmits(['update:cwd'])

const mode = ref('exec')
const command = ref('')
const lines = ref([])
const busy = ref(false)
const inputRef = ref(null)

// ---- 快捷命令（exec）----
function push(type, text) {
  String(text ?? '').split('\n').forEach((l) => lines.value.push({ type, text: l }))
}

async function run() {
  const cmd = command.value
  if (!cmd.trim() || busy.value) return
  push('cmd', '$ ' + cmd)
  command.value = ''
  busy.value = true
  try {
    const res = await api.runCommand(props.connId, cmd)
    if (res.output) push('out', res.output)
    if (res.error) push('err', res.error)
    push('meta', `exit=${res.exitStatus}`)
    if (res.cwd) emit('update:cwd', res.cwd)
  } catch (e) {
    push('err', e.message)
  } finally {
    busy.value = false
    restoreFocus()
    await nextTick()
    const box = document.getElementById('termOut')
    if (box) box.scrollTop = box.scrollHeight
  }
}

// 命令执行后恢复输入框焦点（busy 不再禁用输入框，焦点不会被浏览器夺走）
function restoreFocus() {
  nextTick(() => inputRef.value?.focus())
}

// ---- 交互终端（xterm + SSE）----
const termHost = ref(null)
let xterm = null
let es = null

async function openInteractive() {
  try {
    // pty 尺寸按容器估一个，与 xterm 显示保持一致（创建后不可变）
    const hostEl = termHost.value
    const cols = hostEl ? Math.min(200, Math.max(40, Math.floor(hostEl.clientWidth / 9))) : 100
    const rows = hostEl ? Math.min(60, Math.max(10, Math.floor(hostEl.clientHeight / 18))) : 30
    await api.terminalOpen(props.connId, cols, rows)

    if (!xterm) {
      xterm = new XTerm({
        cursorBlink: true,
        fontSize: 13,
        fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
        theme: { background: '#0f1620', foreground: '#d6e2ef', cursor: '#7fd1ff' },
        scrollback: 2000,
      })
      xterm.open(termHost.value)
      xterm.resize(cols, rows)
      xterm.onData((data) => {
        api.terminalInput(props.connId, data).catch(() => {})
      })
      xterm.writeln('--- 交互终端已连接（可直接输入；Ctrl+C 中断，exit 退出） ---')
    }

    if (!es) {
      es = new EventSource(api.terminalStreamUrl(props.connId))
      es.onmessage = (e) => {
        try {
          const msg = JSON.parse(e.data)
          if (msg.type === 'out' && xterm) xterm.write(msg.data)
        } catch (_) { /* ignore */ }
      }
      es.onerror = () => { /* EventSource 自动重连 */ }
    }
    xterm.focus()
  } catch (e) {
    if (xterm) xterm.writeln('\r\n[交互终端打开失败] ' + e.message)
  }
}

function closeInteractive() {
  if (es) { es.close(); es = null }
  if (xterm) { try { xterm.dispose() } catch (_) {} xterm = null }
}

watch(mode, (m) => {
  if (m === 'interactive') nextTick(openInteractive)
  else closeInteractive()
})

onUnmounted(() => {
  closeInteractive()
  // 通知后端回收 shell（尽量，失败也无妨）
  api.terminalClose(props.connId).catch(() => {})
})
</script>

<template>
  <div class="card term">
    <div class="term-head">
      <h3 class="title">命令终端</h3>
      <el-radio-group v-model="mode" size="small">
        <el-radio-button value="exec">快捷命令</el-radio-button>
        <el-radio-button value="interactive">交互终端</el-radio-button>
      </el-radio-group>
      <el-button
        v-if="mode === 'exec'"
        size="small"
        text
        :disabled="lines.length === 0"
        @click="lines = []"
      >清空</el-button>
    </div>

    <!-- 快捷命令模式 -->
    <template v-if="mode === 'exec'">
      <div id="termOut" class="out">
        <div v-for="(l, i) in lines" :key="i" class="line" :class="l.type">
          {{ l.text }}
        </div>
        <p v-if="lines.length === 0" class="hint">
          输入命令，回车执行。例如 ls -la；需要交互的程序（nano、vim、read 脚本等）请切换到「交互终端」
        </p>
      </div>

      <div class="prompt">
        <span class="prompt-path" :title="cwd">{{ cwd || '/' }}</span>
        <span class="sigil">$</span>
        <el-input
          ref="inputRef"
          v-model="command"
          placeholder="输入命令，回车执行，例如 ls -la"
          autocomplete="off"
          clearable
          @keyup.enter="run"
        >
          <template #append>
            <el-button :loading="busy" @click="run">执行</el-button>
          </template>
        </el-input>
      </div>
    </template>

    <!-- 交互终端模式（xterm.js） -->
    <div v-else class="xterm-wrap" ref="termHost"></div>
  </div>
</template>

<style scoped>
.term {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}
.term-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 10px;
  flex-wrap: wrap;
}
.title {
  margin: 0;
  font-size: 15px;
  color: #1f2d3d;
}
.out {
  flex: 1;
  min-height: 0;
  overflow: auto;
  background: #0f1620;
  border-radius: 10px;
  padding: 10px 12px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12.5px;
  line-height: 1.5;
}
.line {
  white-space: pre-wrap;
  word-break: break-all;
}
.line.cmd {
  color: #7fd1ff;
}
.line.out {
  color: #d6e2ef;
}
.line.err {
  color: #ff8a8a;
}
.line.meta {
  color: #8a97a5;
}
.hint {
  color: #56606c;
  margin: 4px 0;
}
.prompt {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 8px;
}
.prompt-path {
  flex-shrink: 1;
  min-width: 0;
  max-width: 40%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: #2ecc71;
  font-family: ui-monospace, monospace;
  font-size: 12.5px;
}
.sigil {
  color: #2ecc71;
  font-family: ui-monospace, monospace;
  font-weight: 700;
}
.prompt :deep(.el-input) {
  flex: 1;
}
.prompt :deep(.el-input-group__append) {
  background: #0f1620;
}
.prompt :deep(.el-input-group__append .el-button) {
  color: #d6e2ef;
}
.xterm-wrap {
  flex: 1;
  min-height: 0;
  background: #0f1620;
  border-radius: 10px;
  padding: 8px;
  overflow: hidden;
}
.xterm-wrap :deep(.xterm) {
  height: 100%;
}
</style>
