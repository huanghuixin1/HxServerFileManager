<script setup>
import { ref, computed, nextTick, watch, onMounted, onUnmounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Terminal as XTerm } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { api } from '../api.js'
import { useSettings } from '../useSettings.js'

// 终端宏（后端 Data/settings.json）：命名命令片段，点击即发送/填入。按连接（connKey）隔离
// 命令历史：本连接执行过的命令（快捷命令回车 / 交互终端按回车），双击可再次执行，同样按 connKey 隔离
const { macros, history, ensureLoaded, newId, saveMacros, addHistory, clearHistory } = useSettings()
const connMacros = computed(() => macros.value.filter((m) => m.connKey === props.connKey))
const connHistory = computed(() => history.value.filter((h) => h.connKey === props.connKey).slice().reverse())
const historyVisible = ref(false)
const macroMgrVisible = ref(false)
const macroEditVisible = ref(false)
const macroEditing = ref(null) // null = 新增
const macroEditName = ref('')
const macroEditCmd = ref('')

async function runMacro(m) {
  if (mode.value === 'interactive') {
    sendInput(`${m.command}\r`)
    xterm?.focus()
  } else {
    command.value = m.command
    inputRef.value?.focus()
  }
}

function openMacroManager() {
  macroMgrVisible.value = true
}

function startAddMacro() {
  macroEditing.value = null
  macroEditName.value = ''
  macroEditCmd.value = ''
  macroEditVisible.value = true
}

function startEditMacro(m) {
  macroEditing.value = m
  macroEditName.value = m.name
  macroEditCmd.value = m.command
  macroEditVisible.value = true
}

async function saveMacroForm() {
  const name = macroEditName.value.trim()
  const cmd = macroEditCmd.value.trim()
  if (!name || !cmd) {
    ElMessage.warning('名称和命令不能为空')
    return
  }
  try {
    const now = new Date().toISOString()
    if (macroEditing.value) {
      macroEditing.value.name = name
      macroEditing.value.command = cmd
      macroEditing.value.updatedAt = now
    } else {
      macros.value.push({ id: newId(), connKey: props.connKey, name, command: cmd, createdAt: now, updatedAt: now })
    }
    await saveMacros()
    ElMessage.success('已保存')
    macroEditVisible.value = false
    macroEditing.value = null
  } catch (e) {
    ElMessage.error(e.message)
  }
}

async function removeMacro(m) {
  try {
    await ElMessageBox.confirm(`确定删除宏 “${m.name}”？`, '删除宏', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch (_) {
    return
  }
  macros.value = macros.value.filter((x) => x !== m)
  try {
    await saveMacros()
    ElMessage.success('已删除')
  } catch (e) {
    ElMessage.error(e.message)
  }
}

// 两种模式：
//   exec        —— 快捷命令：一次一命令，带 cwd 持久化 + 文件列表联动
//   interactive —— 交互终端：SSH shell + pty（xterm.js），可跑 nano/vim/需要输入的脚本
const props = defineProps({
  connId: String,
  connKey: { type: String, default: '' }, // 连接稳定标识（profileId 或 host@user:port），宏按此隔离
  username: { type: String, default: '' }, // 登录用户名：root 时快捷命令提示符用 #（交互终端由 bash 的 \$ 自行展开）
  cwd: { type: String, default: '/' },
  maximized: { type: Boolean, default: false },
})
const emit = defineEmits(['update:cwd', 'toggle-max', 'disconnected'])

// 提示符符号：root 用 #，其他用 $（与 ssh 登录观感一致）
const sigil = computed(() => (props.username === 'root' ? '#' : '$'))

// 默认交互终端（真终端）；快捷命令保留为二线工具
const mode = ref('interactive')
const command = ref('')
const lines = ref([])
const busy = ref(false)
const inputRef = ref(null)

// 粘贴执行确认：右键粘贴的内容以换行结尾时弹窗，可编辑后决定是否执行
const pasteDraftVisible = ref(false)
const pasteDraft = ref('')

// ---- 快捷命令（exec）----
function push(type, text) {
  String(text ?? '').split('\n').forEach((l) => lines.value.push({ type, text: l }))
}

async function run() {
  const cmd = command.value
  if (!cmd.trim() || busy.value) return
  push('cmd', sigil.value + ' ' + cmd)
  command.value = ''
  busy.value = true
  let exit = -1
  try {
    const res = await api.runCommand(props.connId, cmd)
    if (res.output) push('out', res.output)
    if (res.error) push('err', res.error)
    push('meta', `exit=${res.exitStatus}`)
    exit = res.exitStatus
    if (res.cwd) emit('update:cwd', res.cwd)
  } catch (e) {
    push('err', e.message)
  } finally {
    // 记录到命令历史（失败的命令也记，exit=-1 表示执行异常）
    addHistory(props.connKey, cmd, props.cwd || '/', exit)
    busy.value = false
    restoreFocus()
    await nextTick()
    const box = document.getElementById('termOut')
    if (box) box.scrollTop = box.scrollHeight
  }
}

// ---- 命令历史：交互终端按回车切分记录用户输入的命令行 ----
// 只缓冲用户敲入的内容（onData = 键盘输入，不含服务端回显），控制字符/转义序列一律丢弃；
// 回车时把当前行记入历史。方向键/Alt 组合等会移动光标，缓冲不可靠，直接清空。
let inputLineBuf = ''
function sendInput(data) {
  if (data === '\r' || data === '\n') {
    const line = inputLineBuf.replace(/[\u0000-\u001f\u007f]/g, '').trim()
    inputLineBuf = ''
    if (line) addHistory(props.connKey, line, props.cwd || '/', -1) // 交互终端不知道退出码
  } else if (data === '\x7f' || data === '\b') {
    inputLineBuf = inputLineBuf.slice(0, -1)
  } else if (data === '\x03') {
    inputLineBuf = '' // Ctrl+C：中断当前输入行
  } else if (data.startsWith('\x1b')) {
    inputLineBuf = '' // 方向键/功能键：光标已移动，无法可靠拼行
  } else {
    inputLineBuf += data
  }
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'input', data }))
  }
}

// 历史弹窗：双击某一行再次执行（交互模式直接发到终端并回车；快捷命令模式填入输入框立即执行）
function execHistory(h) {
  if (!h || !h.command) return
  if (mode.value === 'interactive') {
    inputLineBuf = '' // 丢弃可能残留的半行输入，避免拼进历史命令
    sendInput(h.command + '\r')
    xterm?.focus()
  } else {
    command.value = h.command
    run()
  }
}

function fmtTime(iso) {
  if (!iso) return ''
  const d = new Date(iso)
  if (isNaN(d.getTime())) return String(iso)
  const p = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

async function doClearHistory() {
  try {
    await ElMessageBox.confirm('确定清空本连接的全部命令历史？', '清空命令历史', {
      type: 'warning',
      confirmButtonText: '清空',
      cancelButtonText: '取消',
    })
  } catch (_) {
    return
  }
  clearHistory(props.connKey)
  ElMessage.success('已清空')
}

// 命令执行后恢复输入框焦点（busy 不再禁用输入框，焦点不会被浏览器夺走）
function restoreFocus() {
  nextTick(() => inputRef.value?.focus())
}

// ---- 交互终端（xterm + WebSocket 双向通道）----
const termHost = ref(null)
let xterm = null
let ws = null
let manualClose = false // 主动关闭 ws（切 exec / 卸载 / 重连重建），不触发断开提示
let initialCdDone = false // 首次打开时按 cwd prop 恢复目录（刷新/重开回到上次路径）
let oscIgnored = false // 注入恢复路径完成前忽略 OSC 7（防止 shell 初始目录覆盖恢复路径）
// 挂载时快照初始目录：恢复路径不能被后续 OSC 7 推送覆盖（props.cwd 会随 cwdMap 变化）
const initialCwd = props.cwd

// OSC 7 解析：bash PROMPT_COMMAND 每次提示符输出 \x1b]7;file://host/path\x07，
// 从中提取当前目录；chunk 可能被 SSH/TCP 分片，需要跨 chunk 缓冲
let oscBuf = ''
function extractOsc7(chunk) {
  oscBuf += chunk
  let cleaned = ''
  let paths = []
  const re = /\x1b\]7;([^\x07\x1b]*(?:\x07|\x1b\\))/g
  let m
  let last = 0
  while ((m = re.exec(oscBuf))) {
    cleaned += oscBuf.slice(last, m.index)
    let payload = m[1]
    payload = payload.endsWith('\x1b\\') ? payload.slice(0, -2) : payload.slice(0, -1)
    let p = payload.replace(/^file:\/\/[^/]*/, '')
    if (p) {
      try { p = decodeURIComponent(p) } catch (_) { /* 保留原样 */ }
      paths.push(p)
    }
    last = m.index + m[0].length
  }
  cleaned += oscBuf.slice(last)
  // 尾部若还有未闭合的 OSC 7（跨 chunk），保留等待下一条
  const open = cleaned.lastIndexOf('\x1b]7;')
  if (open !== -1) {
    oscBuf = cleaned.slice(open)
    cleaned = cleaned.slice(0, open)
  } else {
    oscBuf = ''
  }
  return { cleaned, paths }
}

// 等容器有实际尺寸（挂载瞬间布局可能未完成，拿 0 会导致 pty 行数列数取下限）
function waitForSize(timeout = 2500) {
  return new Promise((resolve) => {
    const start = Date.now()
    const check = () => {
      const el = termHost.value
      if (el && el.clientHeight > 60 && el.clientWidth > 120) return resolve()
      if (Date.now() - start > timeout) return resolve()
      setTimeout(check, 80)
    }
    check()
  })
}

// 行列数用 FitAddon 按真实单元格尺寸计算，尽量顶满容器（pty 与 xterm 显示使用同一行列，
// 保证 shell 回绕列数一致）
let fitAddon = null

// 容器尺寸变化（窗口缩放 / 拖分隔条 / 终端最大化）时重算行列：
// fit 改显示，ws resize 消息同步 pty，让 shell 回绕列数跟随终端宽度
let sizeObserver = null
let resizeTimer = null
function scheduleRefit() {
  if (!xterm || !fitAddon) return
  if (resizeTimer) clearTimeout(resizeTimer)
  resizeTimer = setTimeout(() => {
    resizeTimer = null
    if (!xterm || !fitAddon) return
    const prevCols = xterm.cols
    const prevRows = xterm.rows
    try { fitAddon.fit() } catch (_) { return } // 容器不可见/无尺寸时静默跳过
    if (xterm.cols === prevCols && xterm.rows === prevRows) return
    if (ws && ws.readyState === WebSocket.OPEN)
      ws.send(JSON.stringify({ type: 'resize', cols: xterm.cols, rows: xterm.rows }))
  }, 150)
}

function startSizeObserver() {
  stopSizeObserver()
  if (!termHost.value) return
  sizeObserver = new ResizeObserver(() => scheduleRefit())
  sizeObserver.observe(termHost.value)
}

function stopSizeObserver() {
  if (sizeObserver) { sizeObserver.disconnect(); sizeObserver = null }
  if (resizeTimer) { clearTimeout(resizeTimer); resizeTimer = null }
}

async function openInteractive() {
  try {
    await waitForSize()

    // 先建好 xterm 并 fit 出与容器一致的真实行列，pty 尺寸随之精确匹配
    if (!xterm) {
      fitAddon = new FitAddon()
      xterm = new XTerm({
        cursorBlink: true,
        fontSize: 13,
        fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
        theme: { background: '#0f1620', foreground: '#d6e2ef', cursor: '#7fd1ff' },
        scrollback: 2000,
        rightClickSelectsWord: false, // 右键留给粘贴用，不选中单词
      })
      xterm.loadAddon(fitAddon)
      xterm.open(termHost.value)
      xterm.onData((data) => {
        sendInput(data)
      })
      // 选中即复制：选区变化时自动写入剪贴板，不用手动 Ctrl+C
      xterm.onSelectionChange(() => {
        const sel = xterm.getSelection()
        if (sel) copyToClipboard(sel)
      })
      // 右键粘贴：接管 contextmenu，从剪贴板读取内容后发送
      termHost.value.addEventListener('contextmenu', onTermContextMenu)
      xterm.writeln('--- 交互终端已连接（可直接输入；Ctrl+C 中断，exit 退出） ---')
    }
    try { fitAddon.fit() } catch (_) { /* 容器无尺寸时保持默认 80x24 */ }
    await api.terminalOpen(props.connId, xterm.cols, xterm.rows)

    // 首次打开时，若 App 给了初始目录（如本地化恢复的路径）且不是根目录，注入一次 cd
    if (!initialCdDone && initialCwd && initialCwd !== '/') {
      initialCdDone = true
      oscIgnored = true // cd 生效前的 OSC 7（shell 初始目录）不推给 App，避免覆盖恢复路径
      setTimeout(() => {
        sendInput(`cd ${initialCwd}\r`)
        setTimeout(() => { oscIgnored = false }, 500) // cd 生效后恢复推送
      }, 400) // 等 shell 提示符就绪
    } else if (!initialCdDone) {
      initialCdDone = true
    }

    if (!ws) {
      manualClose = false
      ws = new WebSocket(api.terminalWsUrl(props.connId))
      ws.onmessage = (e) => {
        try {
          const msg = JSON.parse(e.data)
          if (msg.type === 'out' && xterm) {
            const { cleaned, paths } = extractOsc7(msg.data)
            // 终端 cd 后推送新目录（文件列表跟随）；OSC 序列本身不渲染
            if (paths.length && !oscIgnored) emit('update:cwd', paths[paths.length - 1])
            if (cleaned) xterm.write(cleaned)
          } else if (msg.type === 'closed' && xterm) {
            xterm.writeln('\r\n[终端已关闭] ' + (msg.reason || ''))
          }
        } catch (_) { /* ignore */ }
      }
      // 连接异常/关闭：在终端里写一条醒目提示，并通知 App 把标签标记为断开（红色）
      ws.onclose = () => {
        if (manualClose) { manualClose = false; return } // 主动关闭不提示
        if (xterm) writeDisconnectedBanner()
        emit('disconnected')
      }
      ws.onerror = () => { /* close 回调会处理 */ }
    }
    xterm.focus()
    startSizeObserver()
  } catch (e) {
    if (xterm) xterm.writeln('\r\n[交互终端打开失败] ' + e.message)
  }
}

// ---- 剪贴板：选中即复制 + 右键粘贴（带回车执行确认）----
// 复制到剪贴板：优先 Clipboard API（页面聚焦时无需授权），失败退回隐藏 textarea + execCommand
async function copyToClipboard(text) {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text)
      return
    } catch (_) { /* 权限/上下文不允许，走下面兜底 */ }
  }
  const ta = document.createElement('textarea')
  ta.value = text
  ta.setAttribute('readonly', '')
  ta.style.position = 'fixed'
  ta.style.top = '-9999px'
  ta.style.opacity = '0'
  document.body.appendChild(ta)
  ta.select()
  ta.setSelectionRange(0, text.length)
  try { document.execCommand('copy') } catch (_) {}
  document.body.removeChild(ta)
  xterm?.focus() // 兜底路径短暂移走了焦点，还给终端
}

// 处理待粘贴内容：末尾是换行时弹窗询问是否执行（内容可编辑）
function handlePasteText(text) {
  if (!text) return
  if (/[\r\n]$/.test(text)) {
    pasteDraft.value = text
    pasteDraftVisible.value = true
  } else {
    sendInput(text)
  }
}

// 读取剪贴板并发送（右键触发；右键是用户手势，Clipboard API 可直接读）
async function pasteFromClipboard() {
  let text = ''
  if (navigator.clipboard?.readText) {
    try { text = await navigator.clipboard.readText() } catch (_) { text = '' }
  }
  if (!text) {
    ElMessage.warning('剪贴板为空或无权读取，请用 Ctrl+V 粘贴')
    return
  }
  handlePasteText(text)
  xterm?.focus()
}

// 右键事件：阻止浏览器菜单，改为粘贴
function onTermContextMenu(e) {
  if (!xterm) return
  e.preventDefault()
  e.stopPropagation()
  pasteFromClipboard()
}

// 弹窗「执行」：发送（可能被编辑过的）内容，末尾补回车让 shell 执行
function doPasteExecute() {
  const t = pasteDraft.value
  pasteDraftVisible.value = false
  if (!t) return
  sendInput(/[\r\n]$/.test(t) ? t : t + '\r')
  xterm?.focus()
}

// 弹窗「仅粘贴」：去掉末尾换行后发送，不触发执行
function doPastePlain() {
  const t = pasteDraft.value
  pasteDraftVisible.value = false
  if (!t) return
  sendInput(t.replace(/[\r\n]+$/, ''))
  xterm?.focus()
}

// 在终端里写一条醒目的断开提示（带 ANSI 配色 + 闪烁，尽量显眼）
function writeDisconnectedBanner() {
  if (!xterm) return
  const line = '\r\n'
  xterm.writeln(line + '\x1b[1;5;31m┌────────────────────────────────────────┐\x1b[0m')
  xterm.writeln('\x1b[1;5;31m│   ⚠ SSH 连接已断开                       │\x1b[0m')
  xterm.writeln('\x1b[1;5;31m│   按 R 键重连当前标签                    │\x1b[0m')
  xterm.writeln('\x1b[1;5;31m└────────────────────────────────────────┘\x1b[0m' + line)
}

// 父组件重连成功后调用：重建 WebSocket，恢复输入输出
function reconnect() {
  if (mode.value !== 'interactive') return
  // 关掉旧 ws（可能已 close，再保险一次）；主动关闭，不触发断开提示
  manualClose = true
  if (ws) { try { ws.close() } catch (_) {} ws = null }
  nextTick(() => openInteractive())
}

function closeInteractive() {
  stopSizeObserver()
  manualClose = true // 主动关闭（切 exec 模式 / 卸载），不触发断开提示
  if (ws) { try { ws.close() } catch (_) {} ws = null }
  if (xterm) { try { xterm.dispose() } catch (_) {} xterm = null }
  if (termHost.value) termHost.value.removeEventListener('contextmenu', onTermContextMenu)
  fitAddon = null
}

// 文件列表导航 -> 在交互终端里执行 cd（仅交互模式生效；全屏程序运行时会被吞进程序里，属预期）
function injectCd(path) {
  if (mode.value !== 'interactive') return
  sendInput(`cd ${path}\r`)
}

defineExpose({ injectCd, reconnect })

watch(mode, (m) => {
  if (m === 'interactive') nextTick(openInteractive)
  else closeInteractive()
})

onMounted(() => {
  // 默认交互终端：挂载后立即打开
  if (mode.value === 'interactive') nextTick(openInteractive)
  ensureLoaded()
})

onUnmounted(() => {
  stopSizeObserver()
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
      <el-button
        size="small"
        text
        :title="maximized ? '还原' : '最大化'"
        @click="emit('toggle-max')"
      >
        <el-icon :size="16">
          <FullScreen v-if="!maximized" /><Aim v-else />
        </el-icon>
      </el-button>
    </div>

    <!-- 宏按钮条：交互模式点击直接发送命令，快捷命令模式填入输入框 -->
    <div class="macro-bar">
      <template v-if="connMacros.length">
        <span class="macro-chip" v-for="m in connMacros" :key="m.id" :title="m.command" @click="runMacro(m)">
          <el-icon :size="13" style="margin-right: 4px"><Promotion /></el-icon>{{ m.name }}
        </span>
      </template>
      <span v-else class="macro-hint">这个连接还没有宏，点击「宏设置」添加常用命令（如清日志 / 查看内存）</span>
      <el-button size="small" text @click="historyVisible = true" title="查看本连接执行过的命令，双击可再次执行">
        <el-icon :size="14" style="margin-right: 3px"><Clock /></el-icon>命令历史
      </el-button>
      <el-button size="small" text type="primary" style="margin-left: auto" @click="openMacroManager">
        <el-icon :size="14" style="margin-right: 3px"><Setting /></el-icon>宏设置
      </el-button>
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
        <span class="sigil">{{ sigil }}</span>
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

    <!-- 宏管理列表 -->
    <el-dialog
      v-model="macroMgrVisible"
      title="终端宏"
      width="560px"
      :close-on-click-modal="false"
    >
      <el-table :data="connMacros" empty-text="这个连接还没有宏，点击「新增宏」添加" max-height="320">
        <el-table-column label="名称" width="150">
          <template #default="{ row }">{{ row.name }}</template>
        </el-table-column>
        <el-table-column label="命令" min-width="200">
          <template #default="{ row }">
            <span class="macro-cmd-cell" :title="row.command">{{ row.command }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="130" align="right">
          <template #default="{ row }">
            <el-button size="small" text type="primary" @click="startEditMacro(row)">编辑</el-button>
            <el-button size="small" text type="danger" @click="removeMacro(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
      <template #footer>
        <el-button @click="macroMgrVisible = false">关闭</el-button>
        <el-button type="primary" plain @click="startAddMacro">
          <el-icon style="margin-right: 4px"><Promotion /></el-icon>新增宏
        </el-button>
      </template>
    </el-dialog>

    <!-- 宏新增 / 编辑 -->
    <el-dialog
      v-model="macroEditVisible"
      :title="macroEditing ? '编辑宏' : '新增宏'"
      width="480px"
      :close-on-click-modal="false"
      @closed="macroEditing = null"
    >
      <el-form label-width="56px" @submit.prevent="saveMacroForm">
        <el-form-item label="名称">
          <el-input v-model="macroEditName" placeholder="例如 查看磁盘" />
        </el-form-item>
        <el-form-item label="命令">
          <el-input
            v-model="macroEditCmd"
            type="textarea"
            :rows="3"
            placeholder="例如 free -h && df -h"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="macroEditVisible = false">取消</el-button>
        <el-button type="primary" @click="saveMacroForm">保存</el-button>
      </template>
    </el-dialog>

    <!-- 命令历史：双击一行再次执行（历史按连接隔离，存后端 Data/settings.json） -->
    <el-dialog
      v-model="historyVisible"
      title="命令历史"
      width="680px"
      :close-on-click-modal="false"
    >
      <p class="paste-tip">记录本连接执行过的命令，<b>双击一行</b>（或点「执行」）可再次执行。</p>
      <el-table
        :data="connHistory"
        empty-text="这个连接还没有命令历史"
        max-height="360"
        @row-dblclick="execHistory"
      >
        <el-table-column label="时间" width="150">
          <template #default="{ row }">{{ fmtTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="命令" min-width="240">
          <template #default="{ row }">
            <span class="hist-cmd" :title="row.command">{{ row.command }}</span>
          </template>
        </el-table-column>
        <el-table-column label="目录" min-width="120">
          <template #default="{ row }">
            <span class="hist-cwd" :title="row.cwd || ''">{{ row.cwd || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="80">
          <template #default="{ row }">
            <el-tag v-if="row.exitStatus === 0" size="small" type="success">成功</el-tag>
            <el-tag v-else-if="row.exitStatus > 0" size="small" type="danger">失败</el-tag>
            <el-tag v-else size="small" type="info">未知</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="80" align="right">
          <template #default="{ row }">
            <el-button size="small" text type="primary" @click.stop="execHistory(row)">执行</el-button>
          </template>
        </el-table-column>
      </el-table>
      <template #footer>
        <el-button @click="historyVisible = false">关闭</el-button>
        <el-button type="danger" plain :disabled="connHistory.length === 0" @click="doClearHistory">
          清空本连接历史
        </el-button>
      </template>
    </el-dialog>

    <!-- 粘贴执行确认：右键粘贴的内容以换行结尾时弹窗，可编辑后决定是否执行 -->
    <el-dialog
      v-model="pasteDraftVisible"
      title="粘贴内容以回车结尾，是否执行？"
      width="600px"
      :close-on-click-modal="false"
      @closed="xterm?.focus()"
    >
      <p class="paste-tip">可修改下面内容：<b>执行</b> 发送到终端并回车运行；<b>仅粘贴</b> 只粘贴不执行（去掉末尾回车）。</p>
      <el-input
        v-model="pasteDraft"
        type="textarea"
        :rows="8"
        placeholder="要发送到终端的内容"
        @keydown.ctrl.enter.prevent="doPasteExecute"
      />
      <template #footer>
        <el-button @click="pasteDraftVisible = false">取消</el-button>
        <el-button @click="doPastePlain">仅粘贴</el-button>
        <el-button type="primary" @click="doPasteExecute">执行</el-button>
      </template>
    </el-dialog>
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
.macro-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  margin-bottom: 8px;
  min-height: 26px;
}
.macro-chip {
  display: inline-flex;
  align-items: center;
  cursor: pointer;
  background: #eef6ff;
  color: #2d6cdf;
  border: 1px solid #d3e6ff;
  border-radius: 999px;
  padding: 2px 10px;
  font-size: 12.5px;
  white-space: nowrap;
  transition: background 0.15s;
}
.macro-chip:hover {
  background: #d9ecff;
}
.macro-hint {
  color: #8a97a5;
  font-size: 12.5px;
}
.macro-cmd-cell {
  color: #6b7785;
  font-family: ui-monospace, monospace;
  font-size: 12.5px;
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: middle;
}
.hist-cmd {
  color: #2d6cdf;
  font-family: ui-monospace, monospace;
  font-size: 12.5px;
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: middle;
}
.hist-cwd {
  color: #8a97a5;
  font-size: 12px;
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: middle;
}
.paste-tip {
  margin: 0 0 10px;
  color: #56606c;
  font-size: 13px;
  line-height: 1.6;
}
.xterm-wrap {
  flex: 1;
  min-height: 0;
  background: #0f1620;
  border-radius: 10px;
  padding: 8px 8px 16px; /* 底部多留白，终端滚动到底时最后一行不贴边 */
  overflow: hidden;
}
.xterm-wrap :deep(.xterm) {
  height: 100%;
}
.xterm-wrap :deep(.xterm-viewport) {
  height: 100% !important; /* 滚动区撑满容器，避免只显示 pty 行数高度的上半截 */
}
.xterm-wrap :deep(.xterm-scrollable-element) {
  height: 100%; /* 内容承载元素约束为容器高，滚动才生效（xterm.css 未给它高度） */
}
.xterm-wrap :deep(.xterm-screen) {
  height: 100%;
}
</style>
